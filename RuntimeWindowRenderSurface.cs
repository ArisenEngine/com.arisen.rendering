using ArisenKernel.Contracts;

namespace ArisenEngine.Rendering;

internal sealed class RuntimeWindowRenderSurface : IRenderSurface
{
    private readonly IntPtr m_Handle;
    private readonly uint m_SurfaceId;
    private uint m_Width;
    private uint m_Height;

    public RuntimeWindowRenderSurface(WindowSurfaceInfo windowInfo)
    {
        m_Handle = windowInfo.NativeHandle;
        m_SurfaceId = windowInfo.NativeSurfaceId;
        m_Width = (uint)Math.Max(1, windowInfo.Width);
        m_Height = (uint)Math.Max(1, windowInfo.Height);
    }

    public string Name => "RuntimeMainWindow";
    public IntPtr Handle => m_Handle;
    public uint SurfaceId => m_SurfaceId;
    public uint Width => m_Width;
    public uint Height => m_Height;

    public void Resize(uint width, uint height)
    {
        m_Width = Math.Max(1, width);
        m_Height = Math.Max(1, height);
    }

    public void DisposeSurface()
    {
    }

    public void Dispose()
    {
        DisposeSurface();
    }

    public IntPtr GetHandle() => m_Handle;

    public IntPtr GetSharedHandle(uint frameIndex) => IntPtr.Zero;

    public ulong GetSharedMemorySize(uint frameIndex) => 0;

    public IntPtr GetRenderFinishedSemaphoreHandle(uint frameIndex) => IntPtr.Zero;

    public IntPtr CreateConsumedSemaphoreHandle(uint frameIndex) => IntPtr.Zero;

    public void ReleaseConsumedSemaphoreHandle(IntPtr handle)
    {
    }

    public ulong GetLastRenderTicket() => 0;

    public uint GetLastRenderFrameIndex() => 0;

    public Task WaitForRenderTicketAsync(ulong ticket) => Task.CompletedTask;

    public RenderOutputInfo GetOutputInfo() => new()
    {
        Width = m_Width,
        Height = m_Height
    };

    public void ReportConsumedFrameIndex(uint frameIndex)
    {
    }

    public uint GetLastConsumedFrameIndex() => 0;

    public void OnCreate()
    {
    }

    public void OnResizing()
    {
    }

    public void OnResized()
    {
    }

    public void OnDestroy()
    {
    }
}
