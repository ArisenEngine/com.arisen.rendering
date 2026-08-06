using System;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Threading;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Rendering;

public abstract class RenderPipeline : IDisposable
{
    internal bool disposed;

    private RenderGraph? m_RenderGraph;
    private RenderOutputReadbackPass? m_OutputReadbackPass;
    private RenderGraphTexture? m_PublishedFrameDepth;
    private bool m_OptionalServicesResolved;

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
            m_RenderGraph = CreateRenderGraph(taskGraph);
        }

        ResolveOptionalServices();
        m_PublishedFrameDepth = null;

        // 1. Setup Phase: Derived pipelines register their content passes.
        using (Profiler.Zone("RenderPipeline.SetupGraph"))
        {
            SetupGraph(m_RenderGraph, context);
        }

        var publishedFrameDepth = m_PublishedFrameDepth;
        if (m_OutputReadbackPass?.TryPrepare(context, publishedFrameDepth) == true)
        {
            var capturedFrameDepth = publishedFrameDepth
                ?? throw new InvalidOperationException(
                    "Visual-summary capture began without a published frame-depth texture.");
            using var _ = Profiler.Zone("RenderPipeline.VisualSummaryBoundary");
            m_RenderGraph.AddPass(
                m_OutputReadbackPass,
                builder => builder
                    .ReadTransfer(m_RenderGraph.FrameColor)
                    .ReadTransfer(capturedFrameDepth.Resource));
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
        ulong ticketBeforeExecution = context.Submission.LastTicket;
        ulong submittedTicket;
        try
        {
            submittedTicket = m_RenderGraph.Execute(context);
        }
        catch (Exception executionFailure)
        {
            var failedSubmissionActions = new PostSubmissionActions(this, context);
            RenderFramePostSubmission.ThrowExecutionFailure(
                ref failedSubmissionActions,
                ticketBeforeExecution,
                context.Submission.LastTicket,
                executionFailure);
            throw;
        }

        var postSubmissionActions = new PostSubmissionActions(this, context);
        RenderFramePostSubmission.Execute(ref postSubmissionActions, submittedTicket);
        return submittedTicket;
    }

    /// <summary>
    /// Hook for derived pipelines to define their frame structure by adding passes to the graph.
    /// </summary>
    protected abstract void SetupGraph(RenderGraph graph, RenderContext context);

    protected bool IsVisualSummaryEnabled => m_OutputReadbackPass != null;

    protected void PublishFrameDepth(RenderGraphTexture frameDepth)
    {
        ArgumentNullException.ThrowIfNull(frameDepth);
        if (m_PublishedFrameDepth != null)
        {
            throw new InvalidOperationException(
                "A render pipeline can publish only one primary frame-depth texture per frame.");
        }

        m_PublishedFrameDepth = frameDepth;
    }

    protected virtual RenderGraph CreateRenderGraph(ITaskGraph taskGraph)
    {
        return new RenderGraph(taskGraph);
    }

    protected virtual void OnFrameSubmitted(RenderContext context, ulong submittedTicket)
    {
    }

    protected abstract void OnDisposed();

    public void Dispose()
    {
        m_RenderGraph?.Dispose();
        OnDisposed();
        m_OutputReadbackPass?.Dispose();
        disposed = true;
    }

    internal ulong InternalRender(RenderContext context)
    {
        return Render(context);
    }

    private void ResolveOptionalServices()
    {
        if (m_OptionalServicesResolved)
        {
            return;
        }

        m_OptionalServicesResolved = true;
        if (EngineKernel.Instance.Services.TryGetService<IRuntimeVisualSummaryService>(out var visualSummaryService) &&
            visualSummaryService.IsEnabled)
        {
            m_OutputReadbackPass = new RenderOutputReadbackPass(visualSummaryService);
        }
    }

    private readonly struct PostSubmissionActions : IRenderFramePostSubmissionActions
    {
        private readonly RenderPipeline m_Pipeline;
        private readonly RenderContext m_Context;

        public PostSubmissionActions(RenderPipeline pipeline, RenderContext context)
        {
            m_Pipeline = pipeline;
            m_Context = context;
        }

        public void NotifyFrameSubmitted(ulong submittedTicket)
        {
            m_Pipeline.OnFrameSubmitted(m_Context, submittedTicket);
        }

        public void CompleteReadback(ulong submittedTicket)
        {
            m_Pipeline.m_OutputReadbackPass?.Complete(m_Context, submittedTicket);
        }

        public void AbortReadback(Exception executionFailure)
        {
            m_Pipeline.m_OutputReadbackPass?.Abort(executionFailure);
        }
    }
}
