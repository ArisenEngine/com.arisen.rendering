using System;
using ArisenEngine.Threading;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Rendering;

public abstract class RenderPipeline : IDisposable
{
    internal bool disposed;

    private RenderGraph? m_RenderGraph;

    /// <summary>
    /// Implements the graph-based rendering flow.
    /// This replaces the monolithic Render method.
    /// </summary>
    protected virtual ulong Render(RenderContext context, ReadOnlySpan<Camera> cameras)
    {
        if (m_RenderGraph == null)
        {
            // Acquire the shared TaskGraph from the kernel to enable parallel recording
            var taskGraph = EngineKernel.Instance.Services.GetService<ITaskGraph>();
            m_RenderGraph = new RenderGraph(taskGraph);
        }

        // 1. Setup Phase: Derived pipelines register their passes
        SetupGraph(m_RenderGraph, context, cameras);

        // 2. Execution Phase: Record parallel commands and submit to GPU
        return m_RenderGraph.Execute(context);
    }

    /// <summary>
    /// Hook for derived pipelines to define their frame structure by adding passes to the graph.
    /// </summary>
    protected abstract void SetupGraph(RenderGraph graph, RenderContext context, ReadOnlySpan<Camera> cameras);

    protected abstract void OnDisposed();

    public void Dispose()
    {
        m_RenderGraph?.Dispose();
        OnDisposed();
        disposed = true;
    }

    internal ulong InternalRender(RenderContext context, ReadOnlySpan<Camera> cameras)
    {
        return Render(context, cameras);
    }
}