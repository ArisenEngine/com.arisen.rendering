using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Arisen.DAG;
using ArisenEngine.Threading;
using ArisenEngine.Core.RHI;
using Arisen.Native.RHI;
using ArisenKernel.Services;
using System.Linq;

namespace ArisenEngine.Rendering;

public sealed class RenderGraph : IDisposable
{
    private readonly Graph<RenderPassNode> m_Graph = new();
    private readonly List<RenderResource> m_Resources = new();
    private readonly ITaskGraph m_TaskSystem;
    
    // Key: (ThreadId, SurfaceId), Value: Command Pool for that thread/surface combination
    private readonly ConcurrentDictionary<(int, uint), RHICommandBufferPool> m_CommandPools = new();

    private RHIFactory? m_Factory;
    
    public RenderGraph(ITaskGraph taskSystem)
    {
        m_TaskSystem = taskSystem;
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
    /// Adds a dependency between two passes. (src must execute before dst)
    /// </summary>
    public void AddDependency(RenderPassNode src, RenderPassNode dst)
    {
        m_Graph.Connect(src.Id, 0, dst.Id, 0);
    }

    /// <summary>
    /// Clears all nodes and edges from the graph.
    /// Should be called between frames to prevent pass accumulation.
    /// </summary>
    public void Reset()
    {
        m_Graph.Clear();
        m_Resources.Clear();
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
        foreach (var node in compiled.SortedNodes)
        {
             lastTicket = context.Device.Submit(node.CommandBuffer.Value);
        }

        // 3. Cleanup: Clear the graph for the next frame
        Reset();

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
    }
}
