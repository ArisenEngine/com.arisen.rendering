using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Arisen.DAG;
using ArisenEngine.Threading;
using ArisenEngine.Core.RHI;
using Arisen.Native.RHI;
using ArisenKernel.Services;

namespace ArisenEngine.Rendering;

public sealed class RenderGraph : IDisposable
{
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
    private readonly ITaskGraph m_TaskSystem;
    private uint m_NextResourceId = 1;
    
    // Key: (ThreadId, SurfaceId), Value: Command Pool for that thread/surface combination
    private readonly ConcurrentDictionary<(int, uint), RHICommandBufferPool> m_CommandPools = new();

    private RHIFactory? m_Factory;

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

    private ref ResourceAccessState GetResourceAccess(RenderResource resource)
    {
        ref var access = ref CollectionsMarshal.GetValueRefOrAddDefault(m_ResourceAccess, resource.ResourceId, out var exists);
        if (!exists)
        {
            access = new ResourceAccessState();
        }

        return ref access;
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
        var compiled = GraphCompiler.Compile(m_Graph);
        uint surfaceId = context.SurfaceId;

        try
        {
            return ExecuteCompiled(context, factory, compiled, surfaceId);
        }
        finally
        {
            // Clear transient frame graph state even when command recording/submission fails.
            Reset();
        }
    }

    private ulong ExecuteCompiled(RenderContext context, RHIFactory factory, CompiledGraph<RenderPassNode> compiled, uint surfaceId)
    {
        // 1. Dispatch passes to TaskGraph for parallel command recording
        foreach (var layer in compiled.ParallelLayers)
        {
            if (layer.Count == 0) continue;

            foreach (var node in layer)
            {
                // Wrap execution to handle per-thread pool acquisition
                var recordTask = new ActionTask(() =>
                {
                    int threadId = Thread.CurrentThread.ManagedThreadId;
                    var key = (threadId, surfaceId);
                    
                    // Retrieve or Create a pool for this worker thread/surface
                    if (!m_CommandPools.TryGetValue(key, out var pool))
                    {
                        pool = factory.CreateCommandBufferPool(RHIQueueType.Graphics);
                        m_CommandPools.TryAdd(key, pool);
                    }

                    // Request a unique command buffer for this frame
                    var cmdBuffer = pool.GetCommandBuffer(context.FrameIndex);

                    // Ensure the command buffer is in the recording state
                    cmdBuffer.Begin();
                    
                    node.Setup(context, cmdBuffer);
                    node.Execute();

                    // Finalize the command buffer to the Executable state
                    cmdBuffer.End();
                }, node.Name);

                m_TaskSystem.AddTask(recordTask);
            }

            // Parallel execution across worker threads
            m_TaskSystem.Execute();
        }

        ulong lastTicket = 0;
        // 2. Submit all recorded command buffers in topological order to the GPU
        var sorted = compiled.SortedNodes;
            foreach (var node in sorted)
        {
            if (node.CommandBuffer == null)
            {
                throw new InvalidOperationException($"Render pass '{node.Name}' completed without a recorded command buffer.");
            }

            lastTicket = context.Device.Submit(node.CommandBuffer.Value);
        }

        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[RenderGraph] Execute - Submitted {sorted.Count} nodes. Last Ticket: {lastTicket}");
        }

        return lastTicket;
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
        m_Graph.Clear();
        m_Resources.Clear();
        m_ResourceAccess.Clear();
    }
}
