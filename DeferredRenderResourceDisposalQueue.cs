using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

public sealed class DeferredRenderResourceDisposalQueue
{
    private readonly List<PendingResource> m_PendingResources = new();
    private readonly DeferredRenderResourceDisposalState m_State = new();

    public int PendingCount => m_PendingResources.Count;

    public void BindDevice(RHIDevice device, ulong deviceGeneration)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException(
                "A deferred render-resource queue requires a valid RHI device.",
                nameof(device));
        }

        m_State.Bind(device.Handle, deviceGeneration);
    }

    public void Enqueue(IDisposable resource, ulong submittedTicket)
    {
        if (resource == null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        if (submittedTicket == 0)
        {
            resource.Dispose();
            return;
        }

        ulong deviceGeneration = m_State.GetBoundGeneration();
        m_PendingResources.Add(new PendingResource(
            resource,
            submittedTicket,
            deviceGeneration));
    }

    public void ReleaseCompleted(RHIDevice device, ulong deviceGeneration)
    {
        BindDevice(device, deviceGeneration);
        if (m_PendingResources.Count == 0)
        {
            return;
        }

        using var _ = Profiler.Zone("RenderResourceDisposal.ReleaseCompleted");
        Profiler.PlotValue("RenderResourceDisposal.PendingCount", m_PendingResources.Count);

        ReleaseCompleted(device.GetCompletedTicket(), deviceGeneration);
    }

    public void Drain(
        RHIDevice device,
        ulong deviceGeneration,
        ulong submittedThroughTicket)
    {
        BindDevice(device, deviceGeneration);
        if (m_PendingResources.Count == 0)
        {
            return;
        }

        using var _ = Profiler.Zone("RenderResourceDisposal.Drain");
        Profiler.PlotValue("RenderResourceDisposal.PendingCount", m_PendingResources.Count);

        ulong maximumPendingTicket = 0;
        for (int i = 0; i < m_PendingResources.Count; i++)
        {
            PendingResource pending = m_PendingResources[i];
            m_State.ValidatePendingGeneration(pending.DeviceGeneration);
            maximumPendingTicket = Math.Max(maximumPendingTicket, pending.SubmittedTicket);
        }

        m_State.ValidateDrainBoundary(maximumPendingTicket, submittedThroughTicket);
        device.WaitQueueTicket(maximumPendingTicket);

        ReleaseAll();
    }

    public void ReleaseDevice(
        RHIDevice device,
        ulong deviceGeneration,
        ulong submittedThroughTicket)
    {
        if (!m_State.IsBound)
        {
            if (m_PendingResources.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot release an unbound deferred render-resource queue with {m_PendingResources.Count} pending resources.");
            }

            return;
        }

        Drain(device, deviceGeneration, submittedThroughTicket);
        m_State.Unbind(device.Handle, deviceGeneration, m_PendingResources.Count);
    }

    private void ReleaseCompleted(ulong completedTicket, ulong deviceGeneration)
    {
        m_State.ValidatePendingGeneration(deviceGeneration);
        if (completedTicket == 0)
        {
            return;
        }

        var releasedCount = 0;
        for (int i = m_PendingResources.Count - 1; i >= 0; i--)
        {
            PendingResource pending = m_PendingResources[i];
            m_State.ValidatePendingGeneration(pending.DeviceGeneration);
            if (pending.SubmittedTicket > completedTicket)
            {
                continue;
            }

            pending.Resource.Dispose();
            RemoveAtSwapBack(i);
            releasedCount++;
        }

        if (releasedCount > 0)
        {
            Profiler.PlotValue("RenderResourceDisposal.ReleasedCount", releasedCount);
            Logger.Log(
                $"[RenderResourceDisposal] Released {releasedCount} completed deferred render resources. Pending: {m_PendingResources.Count}");
        }
    }

    private void ReleaseAll()
    {
        var releasedCount = m_PendingResources.Count;
        for (int i = releasedCount - 1; i >= 0; i--)
        {
            PendingResource pending = m_PendingResources[i];
            m_State.ValidatePendingGeneration(pending.DeviceGeneration);
            pending.Resource.Dispose();
        }

        m_PendingResources.Clear();
        Profiler.PlotValue("RenderResourceDisposal.ReleasedCount", releasedCount);
        Logger.Log($"[RenderResourceDisposal] Released {releasedCount} deferred render resources.");
    }

    private void RemoveAtSwapBack(int index)
    {
        var lastIndex = m_PendingResources.Count - 1;
        if (index != lastIndex)
        {
            m_PendingResources[index] = m_PendingResources[lastIndex];
        }

        m_PendingResources.RemoveAt(lastIndex);
    }

    private readonly struct PendingResource
    {
        public readonly IDisposable Resource;
        public readonly ulong SubmittedTicket;
        public readonly ulong DeviceGeneration;

        public PendingResource(
            IDisposable resource,
            ulong submittedTicket,
            ulong deviceGeneration)
        {
            Resource = resource;
            SubmittedTicket = submittedTicket;
            DeviceGeneration = deviceGeneration;
        }
    }
}
