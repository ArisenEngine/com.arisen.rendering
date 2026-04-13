using System;

namespace ArisenEngine.Rendering;

public abstract class RenderPipeline : IDisposable
{
    internal bool disposed;

    /// <summary>
    /// Entry point for the render pipeline to execute its drawing logic.
    /// Returns the GPUTicket of the final submission.
    /// </summary>
    protected abstract ulong Render(RenderContext context, ReadOnlySpan<Camera> cameras);

    protected abstract void OnDisposed();

    public void Dispose()
    {
        OnDisposed();
        disposed = true;
    }

    internal ulong InternalRender(RenderContext context, ReadOnlySpan<Camera> cameras)
    {
        return Render(context, cameras);
    }
}