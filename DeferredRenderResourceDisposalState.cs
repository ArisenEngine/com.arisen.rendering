namespace ArisenEngine.Rendering;

internal sealed class DeferredRenderResourceDisposalState
{
    private IntPtr m_DeviceHandle;
    private ulong m_DeviceGeneration;

    public bool IsBound => m_DeviceHandle != IntPtr.Zero;

    public ulong DeviceGeneration => m_DeviceGeneration;

    public ulong GetBoundGeneration()
    {
        if (!IsBound)
        {
            throw new InvalidOperationException(
                "A deferred render resource was queued before a graphics-device generation was bound.");
        }

        return m_DeviceGeneration;
    }

    public void Bind(IntPtr deviceHandle, ulong deviceGeneration)
    {
        if (deviceHandle == IntPtr.Zero)
        {
            throw new ArgumentException(
                "A deferred render-resource queue requires a valid native device handle.",
                nameof(deviceHandle));
        }
        if (deviceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }

        if (!IsBound)
        {
            m_DeviceHandle = deviceHandle;
            m_DeviceGeneration = deviceGeneration;
            return;
        }

        if (m_DeviceHandle != deviceHandle || m_DeviceGeneration != deviceGeneration)
        {
            throw new InvalidOperationException(
                $"Deferred render-resource queue is still bound to graphics generation " +
                $"{m_DeviceGeneration} (device 0x{m_DeviceHandle.ToInt64():X}) and cannot bind " +
                $"generation {deviceGeneration} (device 0x{deviceHandle.ToInt64():X}).");
        }
    }

    public void ValidatePendingGeneration(ulong pendingGeneration)
    {
        if (!IsBound)
        {
            throw new InvalidOperationException(
                "A deferred render resource was queued before a graphics-device generation was bound.");
        }
        if (pendingGeneration != m_DeviceGeneration)
        {
            throw new InvalidOperationException(
                $"Deferred render resource belongs to graphics generation {pendingGeneration}, " +
                $"but the queue is bound to generation {m_DeviceGeneration}.");
        }
    }

    public void ValidateDrainBoundary(ulong maximumPendingTicket, ulong submittedThroughTicket)
    {
        if (maximumPendingTicket > submittedThroughTicket)
        {
            throw new InvalidOperationException(
                $"Deferred render-resource ticket {maximumPendingTicket} exceeds graphics generation " +
                $"{m_DeviceGeneration}'s last submitted ticket {submittedThroughTicket}.");
        }
    }

    public void Unbind(IntPtr deviceHandle, ulong deviceGeneration, int pendingCount)
    {
        if (!IsBound)
        {
            if (pendingCount != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot release an unbound deferred render-resource queue with {pendingCount} pending resources.");
            }

            return;
        }

        Bind(deviceHandle, deviceGeneration);
        if (pendingCount != 0)
        {
            throw new InvalidOperationException(
                $"Cannot release graphics generation {m_DeviceGeneration} while {pendingCount} deferred resources remain.");
        }

        m_DeviceHandle = IntPtr.Zero;
        m_DeviceGeneration = 0;
    }
}
