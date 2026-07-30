namespace ArisenEngine.Rendering;

internal readonly record struct RenderFrameResourceReservation(
    ulong Epoch,
    ulong Sequence,
    uint SlotIndex,
    ulong PreviousTicket)
{
    public bool IsValid => Epoch != 0;
}

/// <summary>
/// Render-thread-owned reservation state for device-global in-flight resources.
/// </summary>
internal sealed class RenderFrameResourceSlotState
{
    private Slot[] m_Slots = Array.Empty<Slot>();
    private IntPtr m_DeviceHandle;
    private ulong m_DeviceGeneration;
    private ulong m_Epoch;
    private ulong m_NextSequence;

    public RenderFrameResourceReservation Reserve(
        IntPtr deviceHandle,
        ulong deviceGeneration,
        uint slotCount)
    {
        if (deviceHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A frame-resource reservation requires a valid device handle.", nameof(deviceHandle));
        }
        if (deviceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }
        if (slotCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }

        EnsureDevice(deviceHandle, deviceGeneration, slotCount);

        ulong sequence = m_NextSequence++;
        uint slotIndex = checked((uint)(sequence % slotCount));
        ref Slot slot = ref m_Slots[slotIndex];
        if (slot.IsReserved)
        {
            throw new InvalidOperationException(
                $"Frame-resource slot {slotIndex} is already reserved by sequence {slot.Sequence}.");
        }

        var reservation = new RenderFrameResourceReservation(
            m_Epoch,
            sequence,
            slotIndex,
            slot.LastTicket);
        slot.IsReserved = true;
        slot.Sequence = sequence;
        return reservation;
    }

    public void Complete(in RenderFrameResourceReservation reservation, ulong lastTicket)
    {
        ref Slot slot = ref GetReservedSlot(reservation);
        if (lastTicket != 0 && lastTicket <= reservation.PreviousTicket)
        {
            throw new InvalidOperationException(
                $"Frame-resource slot {reservation.SlotIndex} received non-monotonic graphics ticket " +
                $"{lastTicket} after {reservation.PreviousTicket}.");
        }

        slot.LastTicket = lastTicket;
        slot.IsReserved = false;
    }

    public void Cancel(in RenderFrameResourceReservation reservation)
    {
        ref Slot slot = ref GetReservedSlot(reservation);
        slot.LastTicket = reservation.PreviousTicket;
        slot.IsReserved = false;
    }

    public void Reset()
    {
        if (m_Slots.Any(static slot => slot.IsReserved))
        {
            throw new InvalidOperationException(
                "Cannot reset frame-resource ownership while a slot is reserved.");
        }

        m_Slots = Array.Empty<Slot>();
        m_DeviceHandle = IntPtr.Zero;
        m_DeviceGeneration = 0;
        m_NextSequence = 0;
        m_Epoch++;
    }

    private void EnsureDevice(IntPtr deviceHandle, ulong deviceGeneration, uint slotCount)
    {
        bool identityMatches =
            m_DeviceHandle == deviceHandle &&
            m_DeviceGeneration == deviceGeneration &&
            m_Slots.Length == checked((int)slotCount);
        if (identityMatches)
        {
            return;
        }

        if (m_Slots.Any(static slot => slot.IsReserved))
        {
            throw new InvalidOperationException(
                "Cannot replace frame-resource ownership while a slot is reserved.");
        }

        m_DeviceHandle = deviceHandle;
        m_DeviceGeneration = deviceGeneration;
        m_Slots = new Slot[checked((int)slotCount)];
        m_NextSequence = 0;
        m_Epoch++;
        if (m_Epoch == 0)
        {
            m_Epoch = 1;
        }
    }

    private ref Slot GetReservedSlot(in RenderFrameResourceReservation reservation)
    {
        if (!reservation.IsValid || reservation.Epoch != m_Epoch)
        {
            throw new InvalidOperationException("Frame-resource reservation belongs to a stale device epoch.");
        }
        if (reservation.SlotIndex >= (uint)m_Slots.Length)
        {
            throw new InvalidOperationException("Frame-resource reservation references an invalid slot.");
        }

        ref Slot slot = ref m_Slots[reservation.SlotIndex];
        if (!slot.IsReserved || slot.Sequence != reservation.Sequence)
        {
            throw new InvalidOperationException("Frame-resource reservation is stale or already completed.");
        }

        return ref slot;
    }

    private struct Slot
    {
        public ulong Sequence;
        public ulong LastTicket;
        public bool IsReserved;
    }
}
