using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ArisenKernel.Contracts;

namespace ArisenEngine.Rendering;

/// <summary>
/// Defines a deferred command for the RHI.
/// These commands are posted by various threads (UI, Scripting) 
/// and executed exclusively by the Render Thread to ensure thread safety.
/// </summary>
public interface IRHICommand
{
    void Execute(RenderSubsystem subsystem);
}

/// <summary>
/// A thread-safe queue for RHI commands.
/// </summary>
public sealed class RHICommandQueue
{
    private readonly ConcurrentQueue<IRHICommand> m_PendingCommands = new();

    public void Enqueue(IRHICommand command)
    {
        m_PendingCommands.Enqueue(command);
    }

    /// <summary>
    /// Executes all pending commands. 
    /// MUST be called from the Render Thread.
    /// </summary>
    public void ExecutePending(RenderSubsystem subsystem)
    {
        Dictionary<RenderSurfaceRegistration, ResizeSurfaceCommand>? pendingResizes = null;
        List<RenderSurfaceRegistration>? resizeOrder = null;

        while (m_PendingCommands.TryDequeue(out var command))
        {
            if (command is ResizeSurfaceCommand resize)
            {
                pendingResizes ??= new Dictionary<RenderSurfaceRegistration, ResizeSurfaceCommand>();
                resizeOrder ??= new List<RenderSurfaceRegistration>();
                if (pendingResizes.TryAdd(resize.Registration, resize))
                {
                    resizeOrder.Add(resize.Registration);
                }
                else
                {
                    resize.AbsorbCompletions(pendingResizes[resize.Registration]);
                    pendingResizes[resize.Registration] = resize;
                }

                continue;
            }

            if (pendingResizes != null && resizeOrder != null)
            {
                ExecutePendingResizes(subsystem, pendingResizes, resizeOrder);
                pendingResizes.Clear();
                resizeOrder.Clear();
            }

            ExecuteCommand(subsystem, command);
        }

        if (pendingResizes != null && resizeOrder != null)
        {
            ExecutePendingResizes(subsystem, pendingResizes, resizeOrder);
        }
    }

    private static void ExecutePendingResizes(
        RenderSubsystem subsystem,
        IReadOnlyDictionary<RenderSurfaceRegistration, ResizeSurfaceCommand> pendingResizes,
        IReadOnlyList<RenderSurfaceRegistration> resizeOrder)
    {
        for (int index = 0; index < resizeOrder.Count; index++)
        {
            ExecuteCommand(subsystem, pendingResizes[resizeOrder[index]]);
        }
    }

    private static void ExecuteCommand(RenderSubsystem subsystem, IRHICommand command)
    {
        try
        {
            command.Execute(subsystem);
        }
        catch (Exception ex)
        {
            ArisenKernel.Diagnostics.KernelLog.Error(
                $"[RHICommandQueue] Failed to execute {command.GetType().Name}: {ex.Message}");
        }
    }
}

// --- Common Commands ---

public sealed class ResizeSurfaceCommand : IRHICommand
{
    private List<TaskCompletionSource<bool>>? m_Completions;

    public RenderSurfaceRegistration Registration { get; }
    public uint Width { get; }
    public uint Height { get; }

    public ResizeSurfaceCommand(
        RenderSurfaceRegistration registration,
        uint width,
        uint height,
        TaskCompletionSource<bool>? completion = null)
    {
        Registration = registration;
        Width = width;
        Height = height;
        if (completion != null)
        {
            m_Completions = new List<TaskCompletionSource<bool>>(1) { completion };
        }
    }

    public void Execute(RenderSubsystem subsystem)
    {
        try
        {
            bool resized = subsystem.InternalResizeSurface(
                Registration,
                (int)Width,
                (int)Height);
            Complete(resized);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    public void AbsorbCompletions(ResizeSurfaceCommand previous)
    {
        if (previous.m_Completions is not { Count: > 0 })
        {
            return;
        }

        m_Completions ??= new List<TaskCompletionSource<bool>>(previous.m_Completions.Count);
        m_Completions.AddRange(previous.m_Completions);
        previous.m_Completions.Clear();
    }

    private void Complete(bool resized)
    {
        if (m_Completions == null)
        {
            return;
        }

        foreach (var completion in m_Completions)
        {
            completion.TrySetResult(resized);
        }
        m_Completions.Clear();
    }

    private void Fail(Exception exception)
    {
        if (m_Completions == null)
        {
            return;
        }

        foreach (var completion in m_Completions)
        {
            completion.TrySetException(exception);
        }
        m_Completions.Clear();
    }
}

public sealed class RegisterSurfaceCommand : IRHICommand
{
    private readonly TaskCompletionSource<RenderSurfaceRegistration>? m_Completion;

    public IntPtr Host { get; }
    public string Name { get; }
    public SurfaceType SurfaceType { get; }
    public int Width { get; }
    public int Height { get; }

    public RegisterSurfaceCommand(
        IntPtr host,
        string name,
        SurfaceType type,
        int width,
        int height,
        TaskCompletionSource<RenderSurfaceRegistration>? completion = null)
    {
        Host = host;
        Name = name;
        SurfaceType = type;
        Width = width;
        Height = height;
        m_Completion = completion;
    }

    public void Execute(RenderSubsystem subsystem)
    {
        try
        {
            RenderSurfaceRegistration registration = subsystem.InternalRegisterSurface(
                Host,
                Name,
                SurfaceType,
                Width,
                Height);
            m_Completion?.TrySetResult(registration);
        }
        catch (Exception exception)
        {
            m_Completion?.TrySetException(exception);
            throw;
        }
    }
}

public sealed class UnregisterSurfaceCommand : IRHICommand
{
    private readonly TaskCompletionSource<bool>? m_Completion;

    public RenderSurfaceRegistration Registration { get; }

    public UnregisterSurfaceCommand(
        RenderSurfaceRegistration registration,
        TaskCompletionSource<bool>? completion = null)
    {
        Registration = registration;
        m_Completion = completion;
    }

    public void Execute(RenderSubsystem subsystem)
    {
        try
        {
            m_Completion?.TrySetResult(subsystem.InternalUnregisterSurface(Registration));
        }
        catch (Exception exception)
        {
            m_Completion?.TrySetException(exception);
            throw;
        }
    }
}

public sealed class RestartGraphicsBackendCommand : IRHICommand
{
    private readonly IRHIBackend m_Backend;
    private readonly RHIBackendRestartOptions m_Options;
    private readonly TaskCompletionSource<ulong> m_Completion;

    public RestartGraphicsBackendCommand(
        IRHIBackend backend,
        RHIBackendRestartOptions options,
        TaskCompletionSource<ulong> completion)
    {
        m_Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        m_Options = options;
        m_Completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    public void Execute(RenderSubsystem subsystem)
    {
        try
        {
            m_Completion.TrySetResult(
                subsystem.InternalRestartGraphicsBackend(m_Backend, m_Options));
        }
        catch (Exception exception)
        {
            m_Completion.TrySetException(exception);
            throw;
        }
    }
}
