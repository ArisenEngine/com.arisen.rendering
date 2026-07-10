namespace ArisenEngine.Rendering;

internal enum RenderGraphResourceAccessKind
{
    Read,
    Write
}

internal readonly record struct RenderGraphResourceAccess(
    uint ResourceId,
    uint PassNodeId,
    RenderGraphResourceAccessKind Kind,
    RenderResourceState State);

internal readonly record struct RenderGraphResourceTransition(
    uint ResourceId,
    uint BeforePassNodeId,
    RenderResourceState FromState,
    RenderResourceState ToState);

internal static class RenderGraphResourcePlanner
{
    private const int ReadMask = 1;
    private const int WriteMask = 2;

    public static RenderGraphResourceTransition[] BuildTransitionPlan(
        IReadOnlyList<RenderResource> resources,
        IReadOnlyList<uint> sortedNodeIds,
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        Func<uint, string> passNameResolver)
    {
        if (accessEvents.Count == 0 || sortedNodeIds.Count == 0)
        {
            return Array.Empty<RenderGraphResourceTransition>();
        }

        var transitions = new List<RenderGraphResourceTransition>(accessEvents.Count);
        for (int resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var currentState = resource.InitialState;
            var hasKnownState = resource.IsImported && currentState != RenderResourceState.Unknown;
            var hasWriter = resource.IsImported;

            for (int nodeIndex = 0; nodeIndex < sortedNodeIds.Count; nodeIndex++)
            {
                var nodeId = sortedNodeIds[nodeIndex];
                if (!TryGetAccessState(
                        accessEvents,
                        resource.ResourceId,
                        nodeId,
                        resource.ToString(),
                        passNameResolver,
                        out var desiredState,
                        out var accessMask))
                {
                    continue;
                }

                var hasRead = (accessMask & ReadMask) != 0;
                var hasWrite = (accessMask & WriteMask) != 0;
                var passName = passNameResolver(nodeId);

                if (hasRead && !hasWrite && !hasWriter)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' reads resource '{resource}' before any graph pass writes it. " +
                        "Import the resource or add a producing pass before the read.");
                }

                if (!hasKnownState)
                {
                    if (hasRead && !hasWrite)
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{passName}' reads resource '{resource}' while its initial state is unknown.");
                    }

                    transitions.Add(new RenderGraphResourceTransition(
                        resource.ResourceId,
                        nodeId,
                        RenderResourceState.Unknown,
                        desiredState));
                    currentState = desiredState;
                    hasKnownState = true;
                }
                else if (currentState != desiredState)
                {
                    transitions.Add(new RenderGraphResourceTransition(
                        resource.ResourceId,
                        nodeId,
                        currentState,
                        desiredState));
                    currentState = desiredState;
                }

                if (hasWrite)
                {
                    hasWriter = true;
                }
            }
        }

        return transitions.Count == 0 ? Array.Empty<RenderGraphResourceTransition>() : transitions.ToArray();
    }

    public static bool TryGetAccessState(
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        uint resourceId,
        uint nodeId,
        string resourceName,
        Func<uint, string> passNameResolver,
        out RenderResourceState state,
        out int accessMask)
    {
        state = RenderResourceState.Unknown;
        accessMask = 0;
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.ResourceId != resourceId || access.PassNodeId != nodeId)
            {
                continue;
            }

            accessMask |= access.Kind == RenderGraphResourceAccessKind.Read ? ReadMask : WriteMask;
            if (state == RenderResourceState.Unknown)
            {
                state = access.State;
                continue;
            }

            if (state != access.State)
            {
                throw new InvalidOperationException(
                    $"Render pass '{passNameResolver(nodeId)}' declares incompatible states for resource '{resourceName}': {state} and {access.State}.");
            }
        }

        return accessMask != 0;
    }

    public static int GetAccessMask(
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        uint resourceId,
        uint nodeId)
    {
        var mask = 0;
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.ResourceId != resourceId || access.PassNodeId != nodeId)
            {
                continue;
            }

            mask |= access.Kind == RenderGraphResourceAccessKind.Read ? ReadMask : WriteMask;
        }

        return mask;
    }
}
