using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
        while (m_PendingCommands.TryDequeue(out var command))
        {
            try 
            {
                command.Execute(subsystem);
            }
            catch (Exception ex)
            {
                ArisenKernel.Diagnostics.KernelLog.Error($"[RHICommandQueue] Failed to execute {command.GetType().Name}: {ex.Message}");
            }
        }
    }
}

// --- Common Commands ---

public sealed class ResizeSurfaceCommand : IRHICommand
{
    public IntPtr Host { get; }
    public uint Width { get; }
    public uint Height { get; }

    public ResizeSurfaceCommand(IntPtr host, uint width, uint height)
    {
        Host = host;
        Width = width;
        Height = height;
    }

    public void Execute(RenderSubsystem subsystem)
    {
        subsystem.InternalResizeSurface(Host, (int)Width, (int)Height);
    }
}

public sealed class RegisterSurfaceCommand : IRHICommand
{
    public IntPtr Host { get; }
    public string Name { get; }
    public SurfaceType SurfaceType { get; }
    public int Width { get; }
    public int Height { get; }

    public RegisterSurfaceCommand(IntPtr host, string name, SurfaceType type, int width, int height)
    {
        Host = host;
        Name = name;
        SurfaceType = type;
        Width = width;
        Height = height;
    }

    public void Execute(RenderSubsystem subsystem)
    {
        subsystem.InternalRegisterSurface(Host, Name, SurfaceType, Width, Height);
    }
}

public sealed class UnregisterSurfaceCommand : IRHICommand
{
    public IntPtr Host { get; }

    public UnregisterSurfaceCommand(IntPtr host)
    {
        Host = host;
    }

    public void Execute(RenderSubsystem subsystem)
    {
        subsystem.InternalUnregisterSurface(Host);
    }
}
