namespace ArisenEngine.Rendering;

internal readonly record struct RenderGraphTransientTextureLifetime(
    uint ResourceId,
    int FirstPassIndex,
    int LastPassIndex,
    uint FirstPassNodeId,
    uint LastPassNodeId,
    int AccessingPassCount)
{
    public bool IsValid =>
        FirstPassIndex >= 0 &&
        LastPassIndex >= FirstPassIndex &&
        AccessingPassCount > 0;

    public bool Overlaps(in RenderGraphTransientTextureLifetime other)
    {
        return IsValid &&
               other.IsValid &&
               FirstPassIndex <= other.LastPassIndex &&
               other.FirstPassIndex <= LastPassIndex;
    }
}

internal static class RenderGraphResourceLifetimePlanner
{
    public static RenderGraphTransientTextureLifetime[] BuildLifetimePlan(
        IReadOnlyList<RenderResource> resources,
        IReadOnlyList<uint> sortedNodeIds,
        IReadOnlyList<uint> activeNodeIds,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents)
    {
        ValidateActiveNodeOrder(sortedNodeIds, activeNodeIds);

        if (resources.Count == 0 || activeNodeIds.Count == 0 || accessEvents.Count == 0)
        {
            return Array.Empty<RenderGraphTransientTextureLifetime>();
        }

        var lifetimes = new List<RenderGraphTransientTextureLifetime>(resources.Count);
        for (int resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            if (resource.IsImported || resource.Type != RenderResourceType.Texture)
            {
                continue;
            }

            var firstPassIndex = -1;
            var lastPassIndex = -1;
            var firstPassNodeId = 0u;
            var lastPassNodeId = 0u;
            var accessingPassCount = 0;

            for (int passIndex = 0; passIndex < sortedNodeIds.Count; passIndex++)
            {
                var nodeId = sortedNodeIds[passIndex];
                if (!ContainsNode(activeNodeIds, nodeId) ||
                    !PassAccessesResource(accessEvents, resource.ResourceId, nodeId))
                {
                    continue;
                }

                if (firstPassIndex < 0)
                {
                    firstPassIndex = passIndex;
                    firstPassNodeId = nodeId;
                }

                lastPassIndex = passIndex;
                lastPassNodeId = nodeId;
                accessingPassCount++;
            }

            if (firstPassIndex < 0)
            {
                continue;
            }

            var lifetime = new RenderGraphTransientTextureLifetime(
                resource.ResourceId,
                firstPassIndex,
                lastPassIndex,
                firstPassNodeId,
                lastPassNodeId,
                accessingPassCount);
            if (!lifetime.IsValid)
            {
                throw new InvalidOperationException(
                    $"RenderGraph produced an invalid lifetime interval for resource '{resource}'.");
            }

            lifetimes.Add(lifetime);
        }

        return lifetimes.Count == 0
            ? Array.Empty<RenderGraphTransientTextureLifetime>()
            : lifetimes.ToArray();
    }

    public static int GetPeakLiveTextureCount(
        IReadOnlyList<RenderGraphTransientTextureLifetime> lifetimes)
    {
        var peakLiveCount = 0;
        for (int lifetimeIndex = 0; lifetimeIndex < lifetimes.Count; lifetimeIndex++)
        {
            var lifetime = lifetimes[lifetimeIndex];
            if (!lifetime.IsValid)
            {
                throw new InvalidOperationException(
                    $"RenderGraph lifetime {lifetimeIndex} is invalid.");
            }

            var liveCount = 0;
            for (int candidateIndex = 0; candidateIndex < lifetimes.Count; candidateIndex++)
            {
                var candidate = lifetimes[candidateIndex];
                if (!candidate.IsValid)
                {
                    throw new InvalidOperationException(
                        $"RenderGraph lifetime {candidateIndex} is invalid.");
                }

                if (candidate.FirstPassIndex <= lifetime.FirstPassIndex &&
                    candidate.LastPassIndex >= lifetime.FirstPassIndex)
                {
                    liveCount++;
                }
            }

            peakLiveCount = Math.Max(peakLiveCount, liveCount);
        }

        return peakLiveCount;
    }

    private static void ValidateActiveNodeOrder(
        IReadOnlyList<uint> sortedNodeIds,
        IReadOnlyList<uint> activeNodeIds)
    {
        var previousPassIndex = -1;
        for (int activeIndex = 0; activeIndex < activeNodeIds.Count; activeIndex++)
        {
            var passIndex = FindNodeIndex(sortedNodeIds, activeNodeIds[activeIndex]);
            if (passIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Active RenderGraph pass node {activeNodeIds[activeIndex]} is absent from the compiled pass order.");
            }

            if (passIndex <= previousPassIndex)
            {
                throw new InvalidOperationException(
                    "Active RenderGraph pass nodes must be a unique ordered subset of the compiled pass order.");
            }

            previousPassIndex = passIndex;
        }
    }

    private static int FindNodeIndex(IReadOnlyList<uint> nodeIds, uint nodeId)
    {
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (nodeIds[i] == nodeId)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ContainsNode(IReadOnlyList<uint> nodeIds, uint nodeId)
    {
        return FindNodeIndex(nodeIds, nodeId) >= 0;
    }

    private static bool PassAccessesResource(
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        uint resourceId,
        uint nodeId)
    {
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.ResourceId == resourceId && access.PassNodeId == nodeId)
            {
                return true;
            }
        }

        return false;
    }
}
