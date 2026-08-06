using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Arisen.DAG;
using ArisenEngine.Threading;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using Arisen.Native.RHI;
using ArisenKernel.Services;

namespace ArisenEngine.Rendering;

public sealed class RenderGraph : IDisposable
{
    private readonly struct RenderGraphCullingResult
    {
        public RenderGraphCullingResult(uint[] culledNodeIds, string[] culledPassNames)
        {
            CulledNodeIds = culledNodeIds;
            CulledPassNames = culledPassNames;
        }

        public uint[] CulledNodeIds { get; }
        public string[] CulledPassNames { get; }
        public int CulledCount => CulledNodeIds.Length;
    }

    private sealed class CompiledRenderGraphLayout
    {
        public CompiledRenderGraphLayout(
            ulong signature,
            uint[] sortedNodeIds,
            uint[][] parallelLayerNodeIds,
            RenderGraphCullingResult culling)
        {
            Signature = signature;
            SortedNodeIds = sortedNodeIds;
            ParallelLayerNodeIds = parallelLayerNodeIds;
            Culling = culling;
        }

        public ulong Signature { get; }
        public uint[] SortedNodeIds { get; }
        public uint[][] ParallelLayerNodeIds { get; }
        public RenderGraphCullingResult Culling { get; }
    }

    private readonly struct RecordedCommandBufferLease
    {
        public RecordedCommandBufferLease(
            RHICommandBufferPool pool,
            RHICommandBuffer commandBuffer,
            uint frameResourceIndex)
        {
            Pool = pool;
            CommandBuffer = commandBuffer;
            FrameResourceIndex = frameResourceIndex;
        }

        public RHICommandBufferPool Pool { get; }
        public RHICommandBuffer CommandBuffer { get; }
        public uint FrameResourceIndex { get; }
        public bool IsValid => Pool.IsValid && CommandBuffer.IsValid;

        public void Release()
        {
            Pool.ReleaseCommandBuffer(FrameResourceIndex, CommandBuffer.RHIHandle);
        }
    }

    private struct ResourceAccessState
    {
        public RenderPassNode? LastWriter;
        public readonly List<RenderPassNode> Readers;

        public ResourceAccessState()
        {
            LastWriter = null;
            Readers = new List<RenderPassNode>(4);
        }
    }

    private readonly Graph<RenderPassNode> m_Graph = new();
    private readonly List<RenderResource> m_Resources = new();
    private readonly Dictionary<uint, ResourceAccessState> m_ResourceAccess = new();
    private readonly List<RenderGraphResourceAccess> m_ResourceAccessEvents = new();
    private readonly Dictionary<string, RenderGraphTexture> m_TransientTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, RenderGraphTexture> m_TransientTexturesByResourceId = new();
    private readonly HashSet<string> m_FrameTransientTextureNames = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> m_PassWorkItemCounts = new();
    private readonly List<uint> m_ActivePassNodeIds = new();
    private readonly Dictionary<uint, RecordedCommandBufferLease[]> m_CommandBuffers = new();
    private readonly ConcurrentQueue<Exception> m_RecordingErrors = new();
    private readonly ITaskGraph m_TaskSystem;
    private readonly DeferredRenderResourceDisposalQueue? m_DisposalQueue;
    private uint m_NextResourceId = 1;
    
    // Key: (ThreadId, SurfaceId), Value: Command Pool for that thread/surface combination
    private readonly ConcurrentDictionary<(int, uint), RHICommandBufferPool> m_CommandPools = new();

    private RHIFactory? m_Factory;
    private CompiledRenderGraphLayout? m_CachedLayout;
    private ulong m_LastSubmittedTicket;
    private bool m_DiagnosticsLoggedOnce;

    /// <summary>
    /// Graph resource representing the active frame color target supplied by RenderContext.
    /// Pipeline passes should declare reads/writes against this instead of manually ordering
    /// passes that touch the camera/output color image.
    /// </summary>
    public RenderResource FrameColor { get; } = new(
        "FrameColor",
        RenderResourceType.Texture,
        0,
        isImported: true,
        initialState: RenderResourceState.OutputOwnership);

    public RenderGraph(ITaskGraph taskSystem)
        : this(taskSystem, disposalQueue: null)
    {
    }

    public RenderGraph(
        ITaskGraph taskSystem,
        DeferredRenderResourceDisposalQueue? disposalQueue)
    {
        m_TaskSystem = taskSystem;
        m_DisposalQueue = disposalQueue;
        m_Resources.Add(FrameColor);
    }

    /// <summary>
    /// Adds a render pass to the graph.
    /// </summary>
    public T AddPass<T>(T pass) where T : RenderPassNode
    {
        m_Graph.AddNode(pass);
        return pass;
    }

    /// <summary>
    /// Adds a render pass to the graph and lets the caller declare resource usage.
    /// Read/write declarations are converted into graph dependencies automatically.
    /// </summary>
    public T AddPass<T>(T pass, Action<RenderGraphBuilder> configure) where T : RenderPassNode
    {
        AddPass(pass);
        configure(new RenderGraphBuilder(this, pass));
        return pass;
    }

    /// <summary>
    /// Creates a transient resource handle for dependency tracking within the current frame graph.
    /// </summary>
    public RenderResource CreateTransientResource(string name, RenderResourceType type)
    {
        var resource = new RenderResource(name, type, m_NextResourceId++);
        m_Resources.Add(resource);
        return resource;
    }

    public RenderGraphTexture CreateTransientTexture(
        RenderContext context,
        string name,
        RenderGraphTextureDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!m_FrameTransientTextureNames.Add(name))
        {
            throw new InvalidOperationException(
                $"RenderGraph transient texture '{name}' was created more than once in the same frame graph.");
        }

        var texture = m_TransientTextures.TryGetValue(name, out var existingTexture)
            ? existingTexture
            : CreateTransientTexturePoolEntry(name);

        texture.Ensure(
            context.Device.GetFactory(),
            descriptor,
            m_DisposalQueue,
            m_LastSubmittedTicket);
        var resource = new RenderResource(
            name,
            RenderResourceType.Texture,
            m_NextResourceId++,
            initialState: texture.CurrentState);

        texture.Resource = resource;
        m_Resources.Add(resource);
        m_TransientTexturesByResourceId.Add(resource.ResourceId, texture);
        return texture;
    }

    private RenderGraphTexture CreateTransientTexturePoolEntry(string name)
    {
        var texture = new RenderGraphTexture(new RenderResource(name, RenderResourceType.Texture, 0));
        m_TransientTextures.Add(name, texture);
        return texture;
    }

    /// <summary>
    /// Adds a dependency between two passes. (src must execute before dst)
    /// </summary>
    public void AddDependency(RenderPassNode src, RenderPassNode dst)
    {
        if (src.Id == dst.Id)
        {
            return;
        }

        m_Graph.Connect(src.Id, 0, dst.Id, 0);
    }

    internal void RegisterRead(
        RenderPassNode pass,
        RenderResource resource,
        RenderResourceState state,
        RenderAttachmentIntent attachmentIntent = default)
    {
        ValidateResource(pass, resource, "read");
        ValidateResourceState(pass, resource, state);
        RegisterDiagnosticAccess(pass, resource, RenderGraphResourceAccessKind.Read, state, attachmentIntent);
        ref var access = ref GetResourceAccess(resource);

        if (access.LastWriter != null)
        {
            AddDependency(access.LastWriter, pass);
        }

        if (!access.Readers.Any(reader => reader.Id == pass.Id))
        {
            access.Readers.Add(pass);
        }
    }

    internal void RegisterWrite(
        RenderPassNode pass,
        RenderResource resource,
        RenderResourceState state,
        RenderAttachmentIntent attachmentIntent = default)
    {
        ValidateResource(pass, resource, "write");
        ValidateResourceState(pass, resource, state);
        RegisterDiagnosticAccess(pass, resource, RenderGraphResourceAccessKind.Write, state, attachmentIntent);
        ref var access = ref GetResourceAccess(resource);

        if (access.LastWriter != null)
        {
            AddDependency(access.LastWriter, pass);
        }

        foreach (var reader in access.Readers)
        {
            AddDependency(reader, pass);
        }

        access.Readers.Clear();
        access.LastWriter = pass;
    }

    private void RegisterDiagnosticAccess(
        RenderPassNode pass,
        RenderResource resource,
        RenderGraphResourceAccessKind kind,
        RenderResourceState state,
        RenderAttachmentIntent attachmentIntent = default)
    {
        m_ResourceAccessEvents.Add(new RenderGraphResourceAccess(
            resource.ResourceId,
            pass.Id,
            kind,
            state,
            attachmentIntent));
    }

    private ref ResourceAccessState GetResourceAccess(RenderResource resource)
    {
        ref var access = ref CollectionsMarshal.GetValueRefOrAddDefault(m_ResourceAccess, resource.ResourceId, out var exists);
        if (!exists)
        {
            access = new ResourceAccessState();
        }

        return ref access;
    }

    private void ValidateResource(RenderPassNode pass, RenderResource resource, string accessKind)
    {
        for (int i = 0; i < m_Resources.Count; i++)
        {
            var knownResource = m_Resources[i];
            if (knownResource.ResourceId != resource.ResourceId)
            {
                continue;
            }

            if (ReferenceEquals(knownResource, resource))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Render pass '{pass.Name}' declared a {accessKind} for stale or foreign resource '{resource}'. " +
                $"The current graph already owns '{knownResource}' with the same id.");
        }

        throw new InvalidOperationException(
            $"Render pass '{pass.Name}' declared a {accessKind} for unknown resource '{resource}'. " +
            "Create transient resources from the current RenderGraph and do not cache them across frames.");
    }

    private static void ValidateResourceState(
        RenderPassNode pass,
        RenderResource resource,
        RenderResourceState state)
    {
        if (state == RenderResourceState.Unknown)
        {
            throw new InvalidOperationException(
                $"Render pass '{pass.Name}' declared unknown state for resource '{resource}'.");
        }

        if (resource.Type == RenderResourceType.Buffer &&
            (state == RenderResourceState.ColorAttachment ||
             state == RenderResourceState.DepthAttachment ||
             state == RenderResourceState.DepthReadAttachment ||
             state == RenderResourceState.OutputOwnership))
        {
            throw new InvalidOperationException(
                $"Render pass '{pass.Name}' declared {state} for buffer resource '{resource}'.");
        }
    }

    /// <summary>
    /// Wraps the pipeline-authored graph in engine-owned frame target setup/finalization passes.
    /// The setup pass is connected to every current root, and every current leaf is connected to
    /// the final pass so output layout and ownership policy stays out of user render passes.
    /// </summary>
    internal void AddFrameOutputBoundary(RenderPassNode setupPass, RenderPassNode finalPass)
    {
        var userPasses = m_Graph.Nodes.ToArray();

        AddPass(setupPass);
        AddPass(finalPass);
        RegisterDiagnosticAccess(
            setupPass,
            FrameColor,
            RenderGraphResourceAccessKind.Write,
            RenderResourceState.ColorAttachment);
        RegisterDiagnosticAccess(
            finalPass,
            FrameColor,
            RenderGraphResourceAccessKind.Write,
            RenderResourceState.OutputOwnership);

        if (userPasses.Length == 0)
        {
            AddDependency(setupPass, finalPass);
            return;
        }

        var userPassIds = userPasses.Select(pass => pass.Id).ToHashSet();
        var userRootIds = userPassIds.ToHashSet();
        var userLeafIds = userPassIds.ToHashSet();

        foreach (var edge in m_Graph.Edges)
        {
            if (userPassIds.Contains(edge.SourceNodeId) && userPassIds.Contains(edge.TargetNodeId))
            {
                userRootIds.Remove(edge.TargetNodeId);
                userLeafIds.Remove(edge.SourceNodeId);
            }
        }

        foreach (var pass in userPasses)
        {
            if (userRootIds.Contains(pass.Id))
            {
                AddDependency(setupPass, pass);
            }

            if (userLeafIds.Contains(pass.Id))
            {
                AddDependency(pass, finalPass);
            }
        }
    }

    /// <summary>
    /// Clears all nodes and edges from the graph.
    /// Should be called between frames to prevent pass accumulation.
    /// </summary>
    public void Reset()
    {
        m_Graph.Clear();
        m_Resources.Clear();
        m_Resources.Add(FrameColor);
        m_ResourceAccess.Clear();
        m_ResourceAccessEvents.Clear();
        m_TransientTexturesByResourceId.Clear();
        m_FrameTransientTextureNames.Clear();
        m_PassWorkItemCounts.Clear();
        m_ActivePassNodeIds.Clear();
        m_NextResourceId = 1;
    }

    /// <summary>
    /// Compiles and executes the RenderGraph.
    /// Uses the TaskGraph to record commands in parallel.
    /// </summary>
    public ulong Execute(RenderContext context)
    {
        var factory = context.Device.GetFactory();
        m_Factory = factory; // B1: Store factory for safe resource cleanup on Dispose
        var layout = GetOrCompileLayout(out var compileCacheHit);
        ulong ticketBeforeExecution = context.Submission.LastTicket;
        bool diagnosticsEnabled = RenderDiagnostics.IsEnabled(RenderDiagnosticCategory.Graph) &&
            (!m_DiagnosticsLoggedOnce || !compileCacheHit);

        try
        {
            PreparePassWorkItemCounts(context, layout);
            var transitionPlan = BuildResourceTransitionPlan(m_ActivePassNodeIds);
            var lifetimePlan = RenderGraphResourceLifetimePlanner.BuildLifetimePlan(
                m_Resources,
                layout.SortedNodeIds,
                m_ActivePassNodeIds,
                m_ResourceAccessEvents);
            var peakLiveTransientTextureCount =
                RenderGraphResourceLifetimePlanner.GetPeakLiveTextureCount(lifetimePlan);

            Profiler.PlotValue("RenderGraph.PassCount", layout.SortedNodeIds.Length);
            Profiler.PlotValue("RenderGraph.LayerCount", layout.ParallelLayerNodeIds.Length);
            Profiler.PlotValue("RenderGraph.CompileCacheHit", compileCacheHit ? 1 : 0);
            Profiler.PlotValue("RenderGraph.CulledPassCount", layout.Culling.CulledCount);
            Profiler.PlotValue("RenderGraph.ResourceTransitionCount", transitionPlan.Length);
            Profiler.PlotValue("RenderGraph.TransientTextureCount", m_TransientTexturesByResourceId.Count);
            Profiler.PlotValue("RenderGraph.TransientTextureLifetimeCount", lifetimePlan.Length);
            Profiler.PlotValue("RenderGraph.TransientTexturePeakLiveCount", peakLiveTransientTextureCount);
            uint surfaceId = context.SurfaceId;

            if (diagnosticsEnabled)
            {
                LogCompiledGraph(layout, compileCacheHit);
                LogResourceTransitionDiagnostics(transitionPlan);
                LogTransientTextureLifetimeDiagnostics(
                    lifetimePlan,
                    peakLiveTransientTextureCount);
                m_DiagnosticsLoggedOnce = true;
            }

            var submittedTicket = ExecuteCompiled(context, factory, layout, transitionPlan, surfaceId, diagnosticsEnabled);
            UpdateTransientTextureStates(m_ActivePassNodeIds);
            return submittedTicket;
        }
        finally
        {
            RenderGraphSubmissionTicketTracker.CommitAcceptedTicket(
                ref m_LastSubmittedTicket,
                ticketBeforeExecution,
                context.Submission.LastTicket);
            // Clear transient frame graph state even when command recording/submission fails.
            Reset();
        }
    }

    private CompiledRenderGraphLayout GetOrCompileLayout(out bool cacheHit)
    {
        var signature = ComputeTopologySignature();
        if (m_CachedLayout != null && m_CachedLayout.Signature == signature)
        {
            cacheHit = true;
            return m_CachedLayout;
        }

        using (Profiler.Zone("RenderGraph.Compile"))
        {
            try
            {
                var uncullCompiled = GraphCompiler.Compile(m_Graph);
                var culling = CullDeadPasses(uncullCompiled);
                var compiled = culling.CulledCount > 0
                    ? GraphCompiler.Compile(m_Graph)
                    : uncullCompiled;
                m_CachedLayout = CreateLayout(signature, compiled, culling);
                cacheHit = false;
                return m_CachedLayout;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"RenderGraph compile failed. {BuildGraphSummary()}", ex);
            }
        }
    }

    private static CompiledRenderGraphLayout CreateLayout(
        ulong signature,
        CompiledGraph<RenderPassNode> compiled,
        RenderGraphCullingResult culling)
    {
        var sortedNodeIds = new uint[compiled.SortedNodes.Count];
        for (int i = 0; i < compiled.SortedNodes.Count; i++)
        {
            sortedNodeIds[i] = compiled.SortedNodes[i].Id;
        }

        var parallelLayerNodeIds = new uint[compiled.ParallelLayers.Count][];
        for (int layerIndex = 0; layerIndex < compiled.ParallelLayers.Count; layerIndex++)
        {
            var layer = compiled.ParallelLayers[layerIndex];
            var layerNodeIds = new uint[layer.Count];
            for (int nodeIndex = 0; nodeIndex < layer.Count; nodeIndex++)
            {
                layerNodeIds[nodeIndex] = layer[nodeIndex].Id;
            }

            parallelLayerNodeIds[layerIndex] = layerNodeIds;
        }

        return new CompiledRenderGraphLayout(signature, sortedNodeIds, parallelLayerNodeIds, culling);
    }

    private RenderGraphCullingResult CullDeadPasses(CompiledGraph<RenderPassNode> uncullCompiled)
    {
        if (m_Graph.Nodes.Count == 0)
        {
            return new RenderGraphCullingResult(Array.Empty<uint>(), Array.Empty<string>());
        }

        var sortedNodeIds = new uint[uncullCompiled.SortedNodes.Count];
        for (int i = 0; i < uncullCompiled.SortedNodes.Count; i++)
        {
            sortedNodeIds[i] = uncullCompiled.SortedNodes[i].Id;
        }

        var culledNodeIds = RenderGraphPassCullingPlanner.FindCulledPasses(
            sortedNodeIds,
            m_ResourceAccessEvents,
            m_Graph.Edges);
        if (culledNodeIds.Length == 0)
        {
            return new RenderGraphCullingResult(Array.Empty<uint>(), Array.Empty<string>());
        }

        var culledPassNames = new List<string>();
        for (int i = 0; i < culledNodeIds.Length; i++)
        {
            var pass = GetRequiredNode(culledNodeIds[i]);
            culledPassNames.Add($"{pass.Name}#{pass.Id}");
        }

        for (int i = 0; i < culledNodeIds.Length; i++)
        {
            m_Graph.RemoveNode(culledNodeIds[i]);
        }

        return new RenderGraphCullingResult(culledNodeIds.ToArray(), culledPassNames.ToArray());
    }

    private void PreparePassWorkItemCounts(
        RenderContext context,
        CompiledRenderGraphLayout layout)
    {
        m_PassWorkItemCounts.Clear();
        m_ActivePassNodeIds.Clear();

        for (int nodeIndex = 0; nodeIndex < layout.SortedNodeIds.Length; nodeIndex++)
        {
            var node = GetRequiredNode(layout.SortedNodeIds[nodeIndex]);
            int workItemCount;
            try
            {
                workItemCount = node.GetRenderGraphWorkItemCount(context);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Render pass '{node.Name}' failed while reporting work item count.",
                    ex);
            }

            if (workItemCount < 0)
            {
                throw new InvalidOperationException(
                    $"Render pass '{node.Name}' reported invalid work item count {workItemCount}.");
            }

            m_PassWorkItemCounts.Add(node.Id, workItemCount);
            if (workItemCount > 0)
            {
                m_ActivePassNodeIds.Add(node.Id);
            }
        }
    }

    private ulong ExecuteCompiled(
        RenderContext context,
        RHIFactory factory,
        CompiledRenderGraphLayout layout,
        RenderGraphResourceTransition[] transitionPlan,
        uint surfaceId,
        bool diagnosticsEnabled)
    {
        var pendingReleaseFailures = ReleaseRecordedCommandBuffers();
        if (pendingReleaseFailures.Length > 0)
        {
            throw new AggregateException(
                "RenderGraph could not release command buffers retained by the previous execution.",
                pendingReleaseFailures);
        }

        var lastTicket = 0UL;
        Exception? executionFailure = null;

        try
        {
            lastTicket = ExecuteCompiledCore(
                context,
                factory,
                layout,
                transitionPlan,
                surfaceId,
                diagnosticsEnabled);
        }
        catch (Exception ex)
        {
            executionFailure = ex;
        }

        var releaseFailures = ReleaseRecordedCommandBuffers();
        if (executionFailure != null)
        {
            if (releaseFailures.Length > 0)
            {
                var failures = new Exception[releaseFailures.Length + 1];
                failures[0] = executionFailure;
                Array.Copy(releaseFailures, 0, failures, 1, releaseFailures.Length);
                throw new AggregateException(
                    "RenderGraph execution and command-buffer release both failed.",
                    failures);
            }

            ExceptionDispatchInfo.Capture(executionFailure).Throw();
        }

        if (releaseFailures.Length > 0)
        {
            throw new AggregateException(
                "RenderGraph command-buffer release failed after execution.",
                releaseFailures);
        }

        return lastTicket;
    }

    private ulong ExecuteCompiledCore(
        RenderContext context,
        RHIFactory factory,
        CompiledRenderGraphLayout layout,
        RenderGraphResourceTransition[] transitionPlan,
        uint surfaceId,
        bool diagnosticsEnabled)
    {
        m_RecordingErrors.Clear();
        var scheduledWorkItems = 0;
        var submittedWorkItems = 0;
        var skippedPasses = 0;

        // 1. Dispatch passes to TaskGraph for parallel command recording
        for (int layerIndex = 0; layerIndex < layout.ParallelLayerNodeIds.Length; layerIndex++)
        {
            var layer = layout.ParallelLayerNodeIds[layerIndex];
            if (layer.Length == 0) continue;

            using var recordLayerZone = Profiler.Zone("RenderGraph.RecordLayer");
            Profiler.PlotValue("RenderGraph.RecordLayerPassCount", layer.Length);
            var layerWorkItems = 0;
            var layerSkippedPasses = 0;
            StringBuilder? layerDiagnostics = diagnosticsEnabled ? new StringBuilder(128) : null;

            for (int nodeIndex = 0; nodeIndex < layer.Length; nodeIndex++)
            {
                var node = GetRequiredNode(layer[nodeIndex]);
                if (!m_PassWorkItemCounts.TryGetValue(node.Id, out var workItemCount))
                {
                    throw new InvalidOperationException(
                        $"Render pass '{node.Name}' has no prepared work-item count.");
                }

                if (workItemCount <= 0)
                {
                    m_CommandBuffers[node.Id] = Array.Empty<RecordedCommandBufferLease>();
                    skippedPasses++;
                    layerSkippedPasses++;
                    AppendLayerDiagnostic(layerDiagnostics, node, 0);
                    continue;
                }

                var nodeCommandBuffers = new RecordedCommandBufferLease[workItemCount];
                m_CommandBuffers[node.Id] = nodeCommandBuffers;
                scheduledWorkItems += workItemCount;
                layerWorkItems += workItemCount;
                AppendLayerDiagnostic(layerDiagnostics, node, workItemCount);

                for (int workItemIndex = 0; workItemIndex < workItemCount; workItemIndex++)
                {
                    var capturedWorkItemIndex = workItemIndex;
                    RenderPassWorkItem workItem;
                    try
                    {
                        workItem = node.GetRenderGraphWorkItem(context, capturedWorkItemIndex);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{node.Name}' failed while describing work item {capturedWorkItemIndex}.",
                            ex);
                    }

                    // Wrap execution to handle per-thread pool acquisition
                    var recordTask = new ActionTask(() =>
                    {
                        try
                        {
                            using var workItemZone = Profiler.Zone("RenderGraph.RecordWorkItem");

                            int threadId = Thread.CurrentThread.ManagedThreadId;
                            var key = (threadId, surfaceId);

                            // Retrieve or Create a pool for this worker thread/surface
                            if (!m_CommandPools.TryGetValue(key, out var pool))
                            {
                                pool = factory.CreateCommandBufferPool(RHIQueueType.Graphics);
                                if (!m_CommandPools.TryAdd(key, pool))
                                {
                                    factory.ReleaseCommandBufferPool(pool.RHIHandle);
                                    pool = m_CommandPools[key];
                                }
                            }

                            // Request a unique command buffer for this frame
                            var cmdBuffer = pool.GetCommandBuffer(context.FrameResourceIndex);
                            nodeCommandBuffers[capturedWorkItemIndex] = new RecordedCommandBufferLease(
                                pool,
                                cmdBuffer,
                                context.FrameResourceIndex);

                            // Ensure the command buffer is in the recording state
                            cmdBuffer.Begin();
                            try
                            {
                                RecordPlannedTransitionsForPass(context, cmdBuffer, transitionPlan, node.Id, workItem);
                                node.RecordWorkItem(context, cmdBuffer, workItem);
                            }
                            finally
                            {
                                // Finalize the command buffer to the Executable state
                                cmdBuffer.End();
                            }
                        }
                        catch (Exception ex)
                        {
                            m_RecordingErrors.Enqueue(new InvalidOperationException(
                                $"Render pass '{node.Name}' failed while recording work item {FormatWorkItem(workItem)}.",
                                ex));
                        }
                    }, $"{node.Name}[{capturedWorkItemIndex}]");

                    m_TaskSystem.AddTask(recordTask);
                }
            }

            if (diagnosticsEnabled)
            {
                Logger.Log(
                    $"[RenderGraph] Layer {layerIndex}: {layer.Length} passes, {layerWorkItems} work items, " +
                    $"{layerSkippedPasses} skipped. {layerDiagnostics}");
            }

            // Parallel execution across worker threads
            m_TaskSystem.Execute();

            if (!m_RecordingErrors.IsEmpty)
            {
                throw new AggregateException("RenderGraph command recording failed.", m_RecordingErrors);
            }
        }

        Profiler.PlotValue("RenderGraph.WorkItemCount", scheduledWorkItems);
        Profiler.PlotValue("RenderGraph.SkippedPassCount", skippedPasses);

        ulong lastTicket = 0;
        // 2. Submit all recorded command buffers in topological order to the GPU
        using (Profiler.Zone("RenderGraph.Submit"))
        {
            var sorted = layout.SortedNodeIds;
            for (int nodeIndex = 0; nodeIndex < sorted.Length; nodeIndex++)
            {
                var node = GetRequiredNode(sorted[nodeIndex]);
                if (!m_CommandBuffers.TryGetValue(node.Id, out var buffers))
                {
                    throw new InvalidOperationException($"Render pass '{node.Name}' was not scheduled for command recording.");
                }

                for (int i = 0; i < buffers.Length; i++)
                {
                    if (!buffers[i].IsValid)
                    {
                        throw new InvalidOperationException($"Render pass '{node.Name}' work item {i} completed without a recorded command buffer.");
                    }

                    var waitForFrameAcquire = submittedWorkItems == 0;
                    var signalFrameComplete = submittedWorkItems == scheduledWorkItems - 1;
                    lastTicket = context.Submission.SubmitGraphics(
                        buffers[i].CommandBuffer,
                        waitForFrameAcquire,
                        signalFrameComplete);
                    submittedWorkItems++;
                }
            }

            if (diagnosticsEnabled)
            {
                Logger.Log(
                    $"[RenderGraph] Submit: {sorted.Length} passes, {scheduledWorkItems} work items, " +
                    $"{skippedPasses} skipped passes, {submittedWorkItems} submitted. Last Ticket: {lastTicket}");
            }
        }

        return lastTicket;
    }

    private Exception[] ReleaseRecordedCommandBuffers()
    {
        List<Exception>? failures = null;

        foreach (var entry in m_CommandBuffers)
        {
            var leases = entry.Value;
            for (int i = 0; i < leases.Length; i++)
            {
                var lease = leases[i];
                if (!lease.IsValid)
                {
                    continue;
                }

                try
                {
                    lease.Release();
                    leases[i] = default;
                }
                catch (Exception ex)
                {
                    failures ??= new List<Exception>();
                    failures.Add(new InvalidOperationException(
                        $"RenderGraph failed to release command buffer for pass node {entry.Key}, work item {i}.",
                        ex));
                }
            }
        }

        if (failures == null)
        {
            m_CommandBuffers.Clear();
            return Array.Empty<Exception>();
        }

        return failures.ToArray();
    }

    private void RecordPlannedTransitionsForPass(
        RenderContext context,
        RHICommandBuffer commandBuffer,
        RenderGraphResourceTransition[] transitionPlan,
        uint passNodeId,
        RenderPassWorkItem workItem)
    {
        if (transitionPlan.Length == 0 || workItem.Index != 0)
        {
            return;
        }

        for (int i = 0; i < transitionPlan.Length; i++)
        {
            var transition = transitionPlan[i];
            if (transition.BeforePassNodeId != passNodeId)
            {
                continue;
            }

            if (transition.ResourceId != FrameColor.ResourceId)
            {
                if (m_TransientTexturesByResourceId.TryGetValue(transition.ResourceId, out var texture))
                {
                    RecordTransientTextureTransition(commandBuffer, texture, transition);
                }

                continue;
            }

            RecordFrameColorTransition(context, commandBuffer, transition);
        }
    }

    private static void RecordTransientTextureTransition(
        RHICommandBuffer commandBuffer,
        RenderGraphTexture texture,
        RenderGraphResourceTransition transition)
    {
        if (!TryBuildTransientTextureBarrier(texture, transition.FromState, transition.ToState, out var barrier))
        {
            return;
        }

        commandBuffer.PipelineBarrier(
            barrier.SrcStageMask,
            barrier.DstStageMask,
            MemoryMarshal.CreateReadOnlySpan(ref barrier, 1));
    }

    private static void RecordFrameColorTransition(
        RenderContext context,
        RHICommandBuffer commandBuffer,
        RenderGraphResourceTransition transition)
    {
        if (!TryBuildFrameColorBarrier(context, transition.FromState, transition.ToState, out var barrier))
        {
            return;
        }

        commandBuffer.PipelineBarrier(
            barrier.SrcStageMask,
            barrier.DstStageMask,
            MemoryMarshal.CreateReadOnlySpan(ref barrier, 1));
    }

    private static bool TryBuildFrameColorBarrier(
        RenderContext context,
        RenderResourceState fromState,
        RenderResourceState toState,
        out RHIImageMemoryBarrier barrier)
    {
        barrier = default;

        if (fromState == toState)
        {
            return false;
        }

        if (context.TargetImage.IsValid == false)
        {
            return false;
        }

        var from = MapFrameColorState(context, fromState, isSource: true);
        var to = MapFrameColorState(context, toState, isSource: false);
        barrier = new RHIImageMemoryBarrier
        {
            SrcAccessMask = from.Access,
            DstAccessMask = to.Access,
            OldLayout = from.Layout,
            NewLayout = to.Layout,
            SrcQueueFamilyIndex = from.QueueFamily,
            DstQueueFamilyIndex = to.QueueFamily,
            Image = context.TargetImage,
            SubresourceRange = RHIImageSubresourceRange.Color2D(),
            SrcStageMask = from.Stage,
            DstStageMask = to.Stage
        };

        return true;
    }

    private static RenderFrameColorRhiState MapFrameColorState(
        RenderContext context,
        RenderResourceState state,
        bool isSource)
    {
        return state switch
        {
            RenderResourceState.OutputOwnership => RenderFrameColorOutputPolicy.Resolve(
                context.OutputKind,
                context.TargetImageRequiresInitialization,
                isSource),
            RenderResourceState.ColorAttachment => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                EAccessFlag.ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                RHIQueueFamily.Ignored,
                EPipelineStageFlagBits.PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT),
            RenderResourceState.TransferRead => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                EAccessFlag.ACCESS_TRANSFER_READ_BIT,
                RHIQueueFamily.Ignored,
                EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT),
            RenderResourceState.TransferWrite => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                EAccessFlag.ACCESS_TRANSFER_WRITE_BIT,
                RHIQueueFamily.Ignored,
                EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT),
            RenderResourceState.ShaderRead => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                EAccessFlag.ACCESS_SHADER_READ_BIT,
                RHIQueueFamily.Ignored,
                EPipelineStageFlagBits.PIPELINE_STAGE_FRAGMENT_SHADER_BIT),
            _ => throw new InvalidOperationException(
                $"FrameColor does not support graph transition state '{state}'.")
        };
    }

    private static bool TryBuildTransientTextureBarrier(
        RenderGraphTexture texture,
        RenderResourceState fromState,
        RenderResourceState toState,
        out RHIImageMemoryBarrier barrier)
    {
        barrier = default;

        if (fromState == toState)
        {
            return false;
        }

        if (!texture.Image.IsValid)
        {
            return false;
        }

        var from = MapTransientTextureState(fromState, isSource: true);
        var to = MapTransientTextureState(toState, isSource: false);
        barrier = new RHIImageMemoryBarrier
        {
            SrcAccessMask = from.Access,
            DstAccessMask = to.Access,
            OldLayout = from.Layout,
            NewLayout = to.Layout,
            SrcQueueFamilyIndex = RHIQueueFamily.Ignored,
            DstQueueFamilyIndex = RHIQueueFamily.Ignored,
            Image = texture.Image,
            SubresourceRange = new RHIImageSubresourceRange
            {
                AspectMask = texture.AspectMask,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcStageMask = from.Stage,
            DstStageMask = to.Stage
        };

        return true;
    }

    private static RenderTextureRhiState MapTransientTextureState(
        RenderResourceState state,
        bool isSource)
    {
        return state switch
        {
            RenderResourceState.Unknown when isSource => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EAccessFlag.ACCESS_NONE,
                EPipelineStageFlagBits.PIPELINE_STAGE_TOP_OF_PIPE_BIT),
            RenderResourceState.ColorAttachment => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                (EAccessFlag)(
                    (uint)EAccessFlag.ACCESS_COLOR_ATTACHMENT_READ_BIT |
                    (uint)EAccessFlag.ACCESS_COLOR_ATTACHMENT_WRITE_BIT),
                EPipelineStageFlagBits.PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT),
            RenderResourceState.DepthAttachment => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
                EAccessFlag.ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT |
                EAccessFlag.ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
                (EPipelineStageFlagBits)(
                    (uint)EPipelineStageFlagBits.PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT |
                    (uint)EPipelineStageFlagBits.PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT)),
            RenderResourceState.DepthReadAttachment => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL,
                EAccessFlag.ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT,
                (EPipelineStageFlagBits)(
                    (uint)EPipelineStageFlagBits.PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT |
                    (uint)EPipelineStageFlagBits.PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT)),
            RenderResourceState.ShaderRead => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                EAccessFlag.ACCESS_SHADER_READ_BIT,
                EPipelineStageFlagBits.PIPELINE_STAGE_FRAGMENT_SHADER_BIT),
            RenderResourceState.TransferRead => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                EAccessFlag.ACCESS_TRANSFER_READ_BIT,
                EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT),
            RenderResourceState.TransferWrite => new RenderTextureRhiState(
                EImageLayout.IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                EAccessFlag.ACCESS_TRANSFER_WRITE_BIT,
                EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT),
            _ => throw new InvalidOperationException(
                $"Transient texture does not support graph transition state '{state}'.")
        };
    }

    private readonly struct RenderTextureRhiState
    {
        public RenderTextureRhiState(
            EImageLayout layout,
            EAccessFlag access,
            EPipelineStageFlagBits stage)
        {
            Layout = layout;
            Access = access;
            Stage = stage;
        }

        public EImageLayout Layout { get; }
        public EAccessFlag Access { get; }
        public EPipelineStageFlagBits Stage { get; }
    }

    private void UpdateTransientTextureStates(IReadOnlyList<uint> activeNodeIds)
    {
        if (m_TransientTexturesByResourceId.Count == 0)
        {
            return;
        }

        foreach (var pair in m_TransientTexturesByResourceId)
        {
            if (TryGetFinalResourceState(pair.Key, activeNodeIds, out var finalState))
            {
                pair.Value.CurrentState = finalState;
            }
        }
    }

    private bool TryGetFinalResourceState(
        uint resourceId,
        IReadOnlyList<uint> sortedNodeIds,
        out RenderResourceState finalState)
    {
        finalState = RenderResourceState.Unknown;
        var hasState = false;
        for (int nodeIndex = 0; nodeIndex < sortedNodeIds.Count; nodeIndex++)
        {
            if (!TryGetResourceAccessState(
                    resourceId,
                    sortedNodeIds[nodeIndex],
                    out var state,
                    out _))
            {
                continue;
            }

            finalState = state;
            hasState = true;
        }

        return hasState;
    }

    private RenderPassNode GetRequiredNode(uint nodeId)
    {
        var node = m_Graph.GetNode(nodeId);
        if (node == null)
        {
            throw new InvalidOperationException($"Compiled RenderGraph layout references missing pass node {nodeId}.");
        }

        return node;
    }
    private static void AppendLayerDiagnostic(StringBuilder? builder, RenderPassNode node, int workItemCount)
    {
        if (builder == null)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(" | ");
        }

        builder.Append(node.Name);
        builder.Append('#');
        builder.Append(node.Id);
        builder.Append('[');
        builder.Append(workItemCount);
        builder.Append(']');
    }

    private static string FormatWorkItem(RenderPassWorkItem workItem)
    {
        if (!workItem.HasDrawRange)
        {
            return workItem.Index.ToString();
        }

        return $"{workItem.Index} drawRange={workItem.DrawStart}..{workItem.DrawStart + workItem.DrawCount}";
    }

    private void LogCompiledGraph(CompiledRenderGraphLayout layout, bool cacheHit)
    {
        var passOrder = new StringBuilder(128);
        for (int i = 0; i < layout.SortedNodeIds.Length; i++)
        {
            if (i > 0)
            {
                passOrder.Append(" -> ");
            }

            var pass = GetRequiredNode(layout.SortedNodeIds[i]);
            passOrder.Append(pass.Name);
            passOrder.Append('#');
            passOrder.Append(pass.Id);
        }

        Logger.Log($"[RenderGraph] Compiled pass order ({(cacheHit ? "cache hit" : "cache miss")}): {passOrder}");

        for (int layerIndex = 0; layerIndex < layout.ParallelLayerNodeIds.Length; layerIndex++)
        {
            var layer = layout.ParallelLayerNodeIds[layerIndex];
            var layerText = new StringBuilder(64);
            for (int i = 0; i < layer.Length; i++)
            {
                if (i > 0)
                {
                    layerText.Append(", ");
                }

                var pass = GetRequiredNode(layer[i]);
                layerText.Append(pass.Name);
                layerText.Append('#');
                layerText.Append(pass.Id);
            }

            Logger.Log($"[RenderGraph] Compiled layer {layerIndex}: {layerText}");
        }

        LogResourceAccessDiagnostics(layout);
        LogPassCullingDiagnostics(layout.Culling);
    }

    private void LogResourceAccessDiagnostics(CompiledRenderGraphLayout layout)
    {
        if (m_ResourceAccessEvents.Count == 0)
        {
            Logger.Log("[RenderGraph] Resource access: <none>");
            return;
        }

        for (int resourceIndex = 0; resourceIndex < m_Resources.Count; resourceIndex++)
        {
            var resource = m_Resources[resourceIndex];
            var chain = new StringBuilder(128);
            var accessCount = 0;

            for (int nodeIndex = 0; nodeIndex < layout.SortedNodeIds.Length; nodeIndex++)
            {
                var nodeId = layout.SortedNodeIds[nodeIndex];
                var accessMask = GetResourceAccessMask(resource.ResourceId, nodeId);
                if (accessMask == 0)
                {
                    continue;
                }

                if (accessCount > 0)
                {
                    chain.Append(" -> ");
                }

                var pass = GetRequiredNode(nodeId);
                chain.Append(pass.Name);
                chain.Append('#');
                chain.Append(pass.Id);
                chain.Append('[');
                AppendAccessMask(chain, resource.ResourceId, nodeId, accessMask);
                chain.Append(']');
                accessCount++;
            }

            if (accessCount == 0)
            {
                continue;
            }

            Logger.Log(
                $"[RenderGraph] Resource {resource}: {chain} ({Math.Max(0, accessCount - 1)} ordered access edges)");
        }
    }

    private RenderGraphResourceTransition[] BuildResourceTransitionPlan(IReadOnlyList<uint> activeNodeIds)
    {
        return RenderGraphResourcePlanner.BuildTransitionPlan(
            m_Resources,
            activeNodeIds,
            m_ResourceAccessEvents,
            nodeId => GetRequiredNode(nodeId).Name);
    }

    private bool TryGetResourceAccessState(
        uint resourceId,
        uint nodeId,
        out RenderResourceState state,
        out int accessMask)
    {
        return TryGetResourceAccessState(
            resourceId,
            nodeId,
            out state,
            out accessMask,
            out _);
    }

    private bool TryGetResourceAccessState(
        uint resourceId,
        uint nodeId,
        out RenderResourceState state,
        out int accessMask,
        out RenderAttachmentIntent attachmentIntent)
    {
        state = RenderResourceState.Unknown;
        accessMask = 0;
        return RenderGraphResourcePlanner.TryGetAccessState(
            m_ResourceAccessEvents,
            resourceId,
            nodeId,
            GetResourceName(resourceId),
            node => GetRequiredNode(node).Name,
            out state,
            out accessMask,
            out attachmentIntent);
    }

    private void LogPassCullingDiagnostics(RenderGraphCullingResult culling)
    {
        if (culling.CulledCount == 0)
        {
            Logger.Log("[RenderGraph] Culling: 0 culled passes.");
            return;
        }

        var summary = new StringBuilder(96);
        for (int i = 0; i < culling.CulledPassNames.Length; i++)
        {
            if (i > 0)
            {
                summary.Append(", ");
            }

            summary.Append(culling.CulledPassNames[i]);
        }

        Logger.Log($"[RenderGraph] Culling: {culling.CulledCount} culled passes: {summary}");
    }

    private void LogResourceTransitionDiagnostics(RenderGraphResourceTransition[] transitions)
    {
        if (transitions.Length == 0)
        {
            Logger.Log("[RenderGraph] Resource transition plan: <none>");
            return;
        }

        var summary = new StringBuilder(128);
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            if (i > 0)
            {
                summary.Append(" | ");
            }

            var pass = GetRequiredNode(transition.BeforePassNodeId);
            summary.Append(GetResourceName(transition.ResourceId));
            summary.Append(": ");
            summary.Append(transition.FromState);
            summary.Append(" -> ");
            summary.Append(transition.ToState);
            summary.Append(" before ");
            summary.Append(pass.Name);
            summary.Append('#');
            summary.Append(pass.Id);
        }

        Logger.Log($"[RenderGraph] Resource transition plan ({transitions.Length}): {summary}");
    }

    private void LogTransientTextureLifetimeDiagnostics(
        RenderGraphTransientTextureLifetime[] lifetimes,
        int peakLiveTextureCount)
    {
        if (lifetimes.Length == 0)
        {
            Logger.Log("[RenderGraph] Transient texture lifetimes: <none>");
            return;
        }

        Logger.Log(
            $"[RenderGraph] Transient texture lifetimes: {lifetimes.Length} intervals, " +
            $"peak live count {peakLiveTextureCount}.");
        for (int i = 0; i < lifetimes.Length; i++)
        {
            var lifetime = lifetimes[i];
            var firstPass = GetRequiredNode(lifetime.FirstPassNodeId);
            var lastPass = GetRequiredNode(lifetime.LastPassNodeId);
            Logger.Log(
                $"[RenderGraph] Transient texture lifetime {GetResourceName(lifetime.ResourceId)}: " +
                $"compiled pass [{lifetime.FirstPassIndex}..{lifetime.LastPassIndex}], " +
                $"{firstPass.Name}#{firstPass.Id} -> {lastPass.Name}#{lastPass.Id}, " +
                $"{lifetime.AccessingPassCount} active pass accesses.");
        }
    }

    private int GetResourceAccessMask(uint resourceId, uint nodeId)
    {
        return RenderGraphResourcePlanner.GetAccessMask(m_ResourceAccessEvents, resourceId, nodeId);
    }

    private void AppendAccessMask(StringBuilder builder, uint resourceId, uint nodeId, int accessMask)
    {
        var hasRead = (accessMask & 1) != 0;
        var hasWrite = (accessMask & 2) != 0;
        TryGetResourceAccessState(
            resourceId,
            nodeId,
            out var state,
            out _,
            out var attachmentIntent);

        if (hasRead)
        {
            builder.Append("read");
        }

        if (hasWrite)
        {
            if (hasRead)
            {
                builder.Append('/');
            }

            builder.Append("write");
        }

        if (state != RenderResourceState.Unknown)
        {
            builder.Append(':');
            builder.Append(state);
        }

        if (attachmentIntent.IsDeclared)
        {
            builder.Append(",load=");
            builder.Append(attachmentIntent.Load);
            builder.Append(",store=");
            builder.Append(attachmentIntent.Store);
        }
    }

    private string GetResourceName(uint resourceId)
    {
        for (int i = 0; i < m_Resources.Count; i++)
        {
            if (m_Resources[i].ResourceId == resourceId)
            {
                return m_Resources[i].ToString();
            }
        }

        return $"Resource#{resourceId}";
    }

    private string BuildGraphSummary()
    {
        var nodeText = m_Graph.Nodes.Count == 0
            ? "<none>"
            : string.Join(", ", m_Graph.Nodes.Select(node => $"{node.Name}#{node.Id}"));
        var edgeText = m_Graph.Edges.Count == 0
            ? "<none>"
            : string.Join(", ", m_Graph.Edges.Select(edge => $"{edge.SourceNodeId}->{edge.TargetNodeId}"));

        return $"Nodes: {nodeText}. Edges: {edgeText}.";
    }

    private ulong ComputeTopologySignature()
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;

            Mix(ref hash, (ulong)m_Graph.Nodes.Count);
            foreach (var node in m_Graph.Nodes)
            {
                Mix(ref hash, node.Id);
                Mix(ref hash, (ulong)node.GetType().TypeHandle.Value.ToInt64());
                Mix(ref hash, StableStringHash(node.Name));
            }

            Mix(ref hash, (ulong)m_Graph.Edges.Count);
            foreach (var edge in m_Graph.Edges)
            {
                Mix(ref hash, edge.SourceNodeId);
                Mix(ref hash, (uint)edge.SourcePortIndex);
                Mix(ref hash, edge.TargetNodeId);
                Mix(ref hash, (uint)edge.TargetPortIndex);
            }

            Mix(ref hash, (ulong)m_ResourceAccessEvents.Count);
            for (int i = 0; i < m_ResourceAccessEvents.Count; i++)
            {
                var access = m_ResourceAccessEvents[i];
                Mix(ref hash, access.ResourceId);
                Mix(ref hash, access.PassNodeId);
                Mix(ref hash, (uint)access.Kind);
                Mix(ref hash, (uint)access.State);
                Mix(ref hash, (uint)access.AttachmentIntent.Load);
                Mix(ref hash, (uint)access.AttachmentIntent.Store);
            }

            return hash;
        }
    }

    private static void Mix(ref ulong hash, ulong value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
    }

    private static ulong StableStringHash(string value)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < value.Length; i++)
            {
                Mix(ref hash, value[i]);
            }

            return hash;
        }
    }

    public void Dispose()
    {
        // B2: Cleanup all allocated command pools in the native RHI layer
        if (m_Factory != null && m_Factory.Value.IsValid)
        {
            foreach (var pool in m_CommandPools.Values)
            {
                m_Factory.Value.ReleaseCommandBufferPool(pool.RHIHandle);
            }
        }
        
        m_CommandPools.Clear();
        m_CommandBuffers.Clear();
        m_PassWorkItemCounts.Clear();
        m_ActivePassNodeIds.Clear();
        m_RecordingErrors.Clear();
        foreach (var texture in m_TransientTextures.Values)
        {
            texture.DisposeDeferred(m_DisposalQueue, m_LastSubmittedTicket);
        }

        m_TransientTextures.Clear();
        m_TransientTexturesByResourceId.Clear();
        m_FrameTransientTextureNames.Clear();
        m_CachedLayout = null;
        m_Graph.Clear();
        m_Resources.Clear();
        m_ResourceAccess.Clear();
        m_ResourceAccessEvents.Clear();
    }
}
