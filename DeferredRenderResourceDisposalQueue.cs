using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

public sealed class DeferredRenderResourceDisposalQueue
{
    private readonly List<PendingResource> m_PendingResources = new();

    public int PendingCount => m_PendingResources.Count;

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

        m_PendingResources.Add(new PendingResource(resource, submittedTicket));
    }

    public void ReleaseCompleted(RHIDevice device)
    {
        if (m_PendingResources.Count == 0)
        {
            return;
        }

        using var _ = Profiler.Zone("RenderResourceDisposal.ReleaseCompleted");
        Profiler.PlotValue("RenderResourceDisposal.PendingCount", m_PendingResources.Count);

        if (!device.IsValid)
        {
            return;
        }

        ReleaseCompleted(device.GetCompletedTicket());
    }

    public void Drain(RHIDevice device)
    {
        if (m_PendingResources.Count == 0)
        {
            return;
        }

        using var _ = Profiler.Zone("RenderResourceDisposal.Drain");
        Profiler.PlotValue("RenderResourceDisposal.PendingCount", m_PendingResources.Count);

        if (device.IsValid)
        {
            for (int i = 0; i < m_PendingResources.Count; i++)
            {
                device.WaitQueueTicket(m_PendingResources[i].SubmittedTicket);
            }
        }

        ReleaseAll();
    }

    private void ReleaseCompleted(ulong completedTicket)
    {
        if (completedTicket == 0)
        {
            return;
        }

        var releasedCount = 0;
        for (int i = m_PendingResources.Count - 1; i >= 0; i--)
        {
            if (m_PendingResources[i].SubmittedTicket > completedTicket)
            {
                continue;
            }

            m_PendingResources[i].Resource.Dispose();
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
            m_PendingResources[i].Resource.Dispose();
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

        public PendingResource(IDisposable resource, ulong submittedTicket)
        {
            Resource = resource;
            SubmittedTicket = submittedTicket;
        }
    }
}
