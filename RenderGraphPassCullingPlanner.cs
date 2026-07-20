using Arisen.DAG;

namespace ArisenEngine.Rendering;

internal static class RenderGraphPassCullingPlanner
{
    public static uint[] FindCulledPasses(
        IReadOnlyList<uint> sortedNodeIds,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        IReadOnlyList<GraphEdge> dependencies)
    {
        if (sortedNodeIds.Count == 0)
        {
            return Array.Empty<uint>();
        }

        var liveNodeIds = new HashSet<uint>();
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.Kind == RenderGraphResourceAccessKind.Write &&
                access.State == RenderResourceState.OutputOwnership)
            {
                liveNodeIds.Add(access.PassNodeId);
            }
        }

        for (int i = 0; i < sortedNodeIds.Count; i++)
        {
            var nodeId = sortedNodeIds[i];
            if (!PassHasDeclaredWrite(nodeId, accessEvents))
            {
                liveNodeIds.Add(nodeId);
            }
        }

        // Preserve the existing conservative policy when a graph has no observable root.
        if (liveNodeIds.Count == 0)
        {
            return Array.Empty<uint>();
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (int nodeIndex = sortedNodeIds.Count - 1; nodeIndex >= 0; nodeIndex--)
            {
                var nodeId = sortedNodeIds[nodeIndex];
                if (!liveNodeIds.Contains(nodeId))
                {
                    continue;
                }

                if (AddResourceProducersBefore(nodeId, liveNodeIds, sortedNodeIds, accessEvents))
                {
                    changed = true;
                }

                if (PassHasOutputOwnership(nodeId, accessEvents))
                {
                    continue;
                }

                for (int edgeIndex = 0; edgeIndex < dependencies.Count; edgeIndex++)
                {
                    var edge = dependencies[edgeIndex];
                    if (edge.TargetNodeId == nodeId && liveNodeIds.Add(edge.SourceNodeId))
                    {
                        changed = true;
                    }
                }
            }
        }

        var culledNodeIds = new List<uint>();
        for (int i = 0; i < sortedNodeIds.Count; i++)
        {
            var nodeId = sortedNodeIds[i];
            if (!liveNodeIds.Contains(nodeId))
            {
                culledNodeIds.Add(nodeId);
            }
        }

        return culledNodeIds.Count == 0 ? Array.Empty<uint>() : culledNodeIds.ToArray();
    }

    private static bool PassHasDeclaredWrite(
        uint nodeId,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents)
    {
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.PassNodeId == nodeId && access.Kind == RenderGraphResourceAccessKind.Write)
            {
                return true;
            }
        }

        return false;
    }

    private static bool PassHasOutputOwnership(
        uint nodeId,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents)
    {
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.PassNodeId == nodeId &&
                access.Kind == RenderGraphResourceAccessKind.Write &&
                access.State == RenderResourceState.OutputOwnership)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AddResourceProducersBefore(
        uint consumerNodeId,
        HashSet<uint> liveNodeIds,
        IReadOnlyList<uint> sortedNodeIds,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents)
    {
        var changed = false;
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.PassNodeId != consumerNodeId ||
                (access.Kind != RenderGraphResourceAccessKind.Read &&
                 access.State != RenderResourceState.OutputOwnership))
            {
                continue;
            }

            if (TryFindLastResourceWriterBefore(
                    access.ResourceId,
                    consumerNodeId,
                    sortedNodeIds,
                    accessEvents,
                    out var producerNodeId) &&
                liveNodeIds.Add(producerNodeId))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static bool TryFindLastResourceWriterBefore(
        uint resourceId,
        uint consumerNodeId,
        IReadOnlyList<uint> sortedNodeIds,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        out uint producerNodeId)
    {
        producerNodeId = 0;
        var found = false;
        for (int nodeIndex = 0; nodeIndex < sortedNodeIds.Count; nodeIndex++)
        {
            var nodeId = sortedNodeIds[nodeIndex];
            if (nodeId == consumerNodeId)
            {
                return found;
            }

            if (PassWritesResource(nodeId, resourceId, accessEvents))
            {
                producerNodeId = nodeId;
                found = true;
            }
        }

        return false;
    }

    private static bool PassWritesResource(
        uint nodeId,
        uint resourceId,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents)
    {
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.PassNodeId == nodeId &&
                access.ResourceId == resourceId &&
                access.Kind == RenderGraphResourceAccessKind.Write)
            {
                return true;
            }
        }

        return false;
    }
}
