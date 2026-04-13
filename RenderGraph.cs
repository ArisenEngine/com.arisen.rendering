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
    
    // Key: ThreadId, Value: Command Pool for that thread
    private readonly ConcurrentDictionary<int, RHICommandBufferPool> m_CommandPools = new();

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
    /// Compiles and executes the RenderGraph.
    /// Uses the TaskGraph to record commands in parallel.
    /// </summary>
    public ulong Execute(RenderContext context)
    {
        var factory = context.Device.GetFactory();
        var compiled = GraphCompiler.Compile(m_Graph);

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
                    
                    // Retrieve or Create a pool for this worker thread
                    if (!m_CommandPools.TryGetValue(threadId, out var pool))
                    {
                        pool = factory.CreateCommandBufferPool(RHIQueueType.Graphics);
                        m_CommandPools.TryAdd(threadId, pool);
                    }

                    // Request a unique command buffer for this frame
                    var cmdToken = pool.GetCommandBuffer(context.FrameIndex);
                    
                    node.Setup(context, cmdToken);
                    node.Execute();
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
        return lastTicket;
    }


    public void Dispose()
    {
        // Cleanup all allocated command pools
        foreach (var pool in m_CommandPools.Values)
        {
            // We need a reference to the factory to release these pools
            // In a better design, the pool manager would handle this.
        }
        m_CommandPools.Clear();
    }
}
