using System;
using ArisenEngine.Core.Diagnostics;
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
    protected virtual ulong Render(RenderContext context)
    {
        if (m_RenderGraph == null)
        {
            // Acquire the shared TaskGraph from the kernel to enable parallel recording
            var taskGraph = EngineKernel.Instance.Services.GetService<ITaskGraph>();
            m_RenderGraph = new RenderGraph(taskGraph);
        }

        // 1. Setup Phase: Derived pipelines register their content passes.
        using (Profiler.Zone("RenderPipeline.SetupGraph"))
        {
            SetupGraph(m_RenderGraph, context);
        }

        // 2. Engine-owned frame target boundaries wrap the user graph so output
        // acquire/layout/finalization policy is consistent across all pipelines.
        using (Profiler.Zone("RenderPipeline.FrameOutputBoundary"))
        {
            m_RenderGraph.AddFrameOutputBoundary(
                new PrepareFrameTargetPass("PrepareFrameTarget"),
                new FinalOutputPass("FinalOutputPass"));
        }

        // 3. Execution Phase: Record parallel commands and submit to GPU.
        var submittedTicket = m_RenderGraph.Execute(context);
        OnFrameSubmitted(context, submittedTicket);
        return submittedTicket;
    }

    /// <summary>
    /// Hook for derived pipelines to define their frame structure by adding passes to the graph.
    /// </summary>
    protected abstract void SetupGraph(RenderGraph graph, RenderContext context);

    protected virtual void OnFrameSubmitted(RenderContext context, ulong submittedTicket)
    {
    }

    protected abstract void OnDisposed();

    public void Dispose()
    {
        m_RenderGraph?.Dispose();
        OnDisposed();
        disposed = true;
    }

    internal ulong InternalRender(RenderContext context)
    {
        return Render(context);
    }
}
