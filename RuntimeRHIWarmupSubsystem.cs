using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Rendering;

public sealed class RuntimeRHIWarmupSubsystem : IEngineSubsystem
{
#if !ARISEN_ENGINE_EDITOR
    private IRHIBackend? m_Backend;
#endif

    public int Priority => 0;
    public EnginePhase InitPhase => EnginePhase.PostInit;

    public void Initialize()
    {
#if ARISEN_ENGINE_EDITOR
        KernelLog.Info("[RuntimeRHIWarmupSubsystem] Editor build detected; hardware warmup is owned by the editor boot pipeline.");
#else
        var services = EngineKernel.Instance.Services;
        if (!services.TryGetService<IRHIBackend>(out m_Backend) || m_Backend == null)
        {
            throw new InvalidOperationException("Runtime RHI warmup requires IRHIBackend, but no backend service is registered.");
        }

        KernelLog.InfoFormat("[RuntimeRHIWarmupSubsystem] Initializing selected RHI backend: {0}", m_Backend.Name);
        if (!m_Backend.Initialize(services))
        {
            throw new InvalidOperationException($"Runtime RHI backend initialization failed ({m_Backend.Name}). See log for details.");
        }

        KernelLog.InfoFormat("[RuntimeRHIWarmupSubsystem] RHI backend initialized: {0}", m_Backend.Name);
#endif
    }

    public void Shutdown()
    {
#if !ARISEN_ENGINE_EDITOR
        if (m_Backend?.IsInitialized == true)
        {
            KernelLog.InfoFormat("[RuntimeRHIWarmupSubsystem] Shutting down RHI backend: {0}", m_Backend.Name);
            m_Backend.Shutdown();
        }

        m_Backend = null;
#endif
    }

    public void Dispose()
    {
        Shutdown();
    }
}
