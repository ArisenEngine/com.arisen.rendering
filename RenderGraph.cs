using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    private const uint DiagnosticsFrameInterval = 60;

    private enum RenderResourceAccessKind
    {
        Read,
        Write
    }

    private readonly struct RenderResourceAccess
    {
        public RenderResourceAccess(uint resourceId, uint passNodeId, RenderResourceAccessKind kind)
        {
            ResourceId = resourceId;
            PassNodeId = passNodeId;
            Kind = kind;
        }

        public uint ResourceId { get; }
        public uint PassNodeId { get; }
        public RenderResourceAccessKind Kind { get; }
    }

    private sealed class CompiledRenderGraphLayout
    {
        public CompiledRenderGraphLayout(ulong signature, uint[] sortedNodeIds, uint[][] parallelLayerNodeIds)
        {
            Signature = signature;
            SortedNodeIds = sortedNodeIds;
            ParallelLayerNodeIds = parallelLayerNodeIds;
        }

        public ulong Signature { get; }
        public uint[] SortedNodeIds { get; }
        public uint[][] ParallelLayerNodeIds { get; }
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
    private readonly List<RenderResourceAccess> m_ResourceAccessEvents = new();
    private readonly Dictionary<uint, RHICommandBuffer[]> m_CommandBuffers = new();
    private readonly ConcurrentQueue<Exception> m_RecordingErrors = new();
    private readonly ITaskGraph m_TaskSystem;
    private uint m_NextResourceId = 1;
    
    // Key: (ThreadId, SurfaceId), Value: Command Pool for that thread/surface combination
    private readonly ConcurrentDictionary<(int, uint), RHICommandBufferPool> m_CommandPools = new();

    private RHIFactory? m_Factory;
    private CompiledRenderGraphLayout? m_CachedLayout;
    private bool m_DiagnosticsLoggedOnce;

    /// <summary>
    /// Graph resource representing the active frame color target supplied by RenderContext.
    /// Pipeline passes should declare reads/writes against this instead of manually ordering
    /// passes that touch the camera/output color image.
    /// </summary>
    public RenderResource FrameColor { get; } = new("FrameColor", RenderResourceType.Texture, 0);
    
    public RenderGraph(ITaskGraph taskSystem)
    {
        m_TaskSystem = taskSystem;
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

    internal void RegisterRead(RenderPassNode pass, RenderResource resource)
    {
        ValidateResource(pass, resource, "read");
        RegisterDiagnosticAccess(pass, resource, RenderResourceAccessKind.Read);
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

    internal void RegisterWrite(RenderPassNode pass, RenderResource resource)
    {
        ValidateResource(pass, resource, "write");
        RegisterDiagnosticAccess(pass, resource, RenderResourceAccessKind.Write);
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

    private void RegisterDiagnosticAccess(RenderPassNode pass, RenderResource resource, RenderResourceAccessKind kind)
    {
        m_ResourceAccessEvents.Add(new RenderResourceAccess(resource.ResourceId, pass.Id, kind));
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
        RegisterDiagnosticAccess(setupPass, FrameColor, RenderResourceAccessKind.Write);
        RegisterDiagnosticAccess(finalPass, FrameColor, RenderResourceAccessKind.Read);

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
        var diagnosticsEnabled = ShouldLogDiagnostics(context);
        var layout = GetOrCompileLayout(out var compileCacheHit);

        Profiler.PlotValue("RenderGraph.PassCount", layout.SortedNodeIds.Length);
        Profiler.PlotValue("RenderGraph.LayerCount", layout.ParallelLayerNodeIds.Length);
        Profiler.PlotValue("RenderGraph.CompileCacheHit", compileCacheHit ? 1 : 0);
        Profiler.PlotValue("RenderGraph.CulledPassCount", 0);
        uint surfaceId = context.SurfaceId;

        if (diagnosticsEnabled)
        {
            LogCompiledGraph(layout, compileCacheHit);
            m_DiagnosticsLoggedOnce = true;
        }

        try
        {
            return ExecuteCompiled(context, factory, layout, surfaceId, diagnosticsEnabled);
        }
        finally
        {
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
                var compiled = GraphCompiler.Compile(m_Graph);
                m_CachedLayout = CreateLayout(signature, compiled);
                cacheHit = false;
                return m_CachedLayout;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"RenderGraph compile failed. {BuildGraphSummary()}", ex);
            }
        }
    }

    private static CompiledRenderGraphLayout CreateLayout(ulong signature, CompiledGraph<RenderPassNode> compiled)
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

        return new CompiledRenderGraphLayout(signature, sortedNodeIds, parallelLayerNodeIds);
    }

    private ulong ExecuteCompiled(
        RenderContext context,
        RHIFactory factory,
        CompiledRenderGraphLayout layout,
        uint surfaceId,
        bool diagnosticsEnabled)
    {
        m_CommandBuffers.Clear();
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
                int workItemCount;
                try
                {
                    workItemCount = node.GetRenderGraphWorkItemCount(context);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Render pass '{node.Name}' failed while reporting work item count.", ex);
                }

                if (workItemCount < 0)
                {
                    throw new InvalidOperationException($"Render pass '{node.Name}' reported invalid work item count {workItemCount}.");
                }

                if (workItemCount <= 0)
                {
                    m_CommandBuffers[node.Id] = Array.Empty<RHICommandBuffer>();
                    skippedPasses++;
                    layerSkippedPasses++;
                    AppendLayerDiagnostic(layerDiagnostics, node, 0);
                    continue;
                }

                var nodeCommandBuffers = new RHICommandBuffer[workItemCount];
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
                            var cmdBuffer = pool.GetCommandBuffer(context.FrameIndex);
                            nodeCommandBuffers[capturedWorkItemIndex] = cmdBuffer;

                            // Ensure the command buffer is in the recording state
                            cmdBuffer.Begin();
                            try
                            {
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
                        buffers[i],
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

    private bool ShouldLogDiagnostics(RenderContext context)
    {
        return !m_DiagnosticsLoggedOnce || context.FrameIndex % DiagnosticsFrameInterval == 0;
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
        Logger.Log(
            "[RenderGraph] Culling: 0 culled passes; pass culling planner is not enabled for the current runtime slice.");
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
                AppendAccessMask(chain, accessMask);
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

    private int GetResourceAccessMask(uint resourceId, uint nodeId)
    {
        var mask = 0;
        for (int i = 0; i < m_ResourceAccessEvents.Count; i++)
        {
            var access = m_ResourceAccessEvents[i];
            if (access.ResourceId != resourceId || access.PassNodeId != nodeId)
            {
                continue;
            }

            mask |= access.Kind == RenderResourceAccessKind.Read ? 1 : 2;
        }

        return mask;
    }

    private static void AppendAccessMask(StringBuilder builder, int accessMask)
    {
        var hasRead = (accessMask & 1) != 0;
        var hasWrite = (accessMask & 2) != 0;

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
        m_RecordingErrors.Clear();
        m_CachedLayout = null;
        m_Graph.Clear();
        m_Resources.Clear();
        m_ResourceAccess.Clear();
        m_ResourceAccessEvents.Clear();
    }
}
