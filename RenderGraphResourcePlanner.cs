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
    RenderResourceState State,
    RenderAttachmentIntent AttachmentIntent = default);

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
            var hasKnownState = currentState != RenderResourceState.Unknown;
            var hasStoredContent = resource.IsImported;

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
                        out var accessMask,
                        out var attachmentIntent))
                {
                    continue;
                }

                var hasRead = (accessMask & ReadMask) != 0;
                var hasWrite = (accessMask & WriteMask) != 0;
                var passName = passNameResolver(nodeId);
                ValidateAttachmentIntent(
                    resource,
                    passName,
                    desiredState,
                    hasRead,
                    hasWrite,
                    attachmentIntent);

                if (RequiresExistingAttachmentContent(attachmentIntent) && !hasStoredContent)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' loads resource '{resource}' before any graph pass stores it. " +
                        "Import the resource, add a stored producing pass, or initialize it with Clear/Don'tCare.");
                }

                if (hasRead && !hasWrite && !hasStoredContent)
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

                if (attachmentIntent.IsDeclared)
                {
                    hasStoredContent = attachmentIntent.Store == RenderAttachmentStoreIntent.Store;
                }
                else if (hasWrite)
                {
                    hasStoredContent = true;
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
        return TryGetAccessState(
            accessEvents,
            resourceId,
            nodeId,
            resourceName,
            passNameResolver,
            out state,
            out accessMask,
            out _);
    }

    public static bool TryGetAccessState(
        IReadOnlyList<RenderGraphResourceAccess> accessEvents,
        uint resourceId,
        uint nodeId,
        string resourceName,
        Func<uint, string> passNameResolver,
        out RenderResourceState state,
        out int accessMask,
        out RenderAttachmentIntent attachmentIntent)
    {
        state = RenderResourceState.Unknown;
        accessMask = 0;
        attachmentIntent = default;
        var hasAccess = false;
        for (int i = 0; i < accessEvents.Count; i++)
        {
            var access = accessEvents[i];
            if (access.ResourceId != resourceId || access.PassNodeId != nodeId)
            {
                continue;
            }

            if (hasAccess && attachmentIntent != access.AttachmentIntent)
            {
                throw new InvalidOperationException(
                    $"Render pass '{passNameResolver(nodeId)}' declares incompatible attachment intents for resource '{resourceName}': " +
                    $"{FormatAttachmentIntent(attachmentIntent)} and {FormatAttachmentIntent(access.AttachmentIntent)}.");
            }

            accessMask |= access.Kind == RenderGraphResourceAccessKind.Read ? ReadMask : WriteMask;
            if (!hasAccess)
            {
                state = access.State;
                attachmentIntent = access.AttachmentIntent;
                hasAccess = true;
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

    private static void ValidateAttachmentIntent(
        RenderResource resource,
        string passName,
        RenderResourceState state,
        bool hasRead,
        bool hasWrite,
        RenderAttachmentIntent intent)
    {
        if (!intent.IsDeclared)
        {
            return;
        }

        if (!IsAttachmentState(state))
        {
            throw new InvalidOperationException(
                $"Render pass '{passName}' declares attachment intent {FormatAttachmentIntent(intent)} " +
                $"for non-attachment state {state} on resource '{resource}'.");
        }

        if (intent.Load == RenderAttachmentLoadIntent.None ||
            intent.Store == RenderAttachmentStoreIntent.None)
        {
            throw new InvalidOperationException(
                $"Render pass '{passName}' must declare both load and store intent for attachment resource '{resource}'.");
        }

        if (state == RenderResourceState.DepthReadAttachment && hasWrite)
        {
            throw new InvalidOperationException(
                $"Render pass '{passName}' declares write access for read-only depth attachment '{resource}'.");
        }

        switch (intent.Load)
        {
            case RenderAttachmentLoadIntent.Clear:
                if (!hasWrite)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' clears attachment '{resource}' without write access.");
                }

                if (hasRead)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' declares read access while clearing attachment '{resource}'. " +
                        "Use ClearThenLoad for a pass whose later work items load the cleared result.");
                }
                break;

            case RenderAttachmentLoadIntent.DontCare:
                if (!hasWrite || hasRead)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' uses Don'tCare load intent for attachment '{resource}' without write-only access.");
                }
                break;

            case RenderAttachmentLoadIntent.Load:
                if (!hasRead || !hasWrite)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' loads attachment '{resource}' without read/write access.");
                }
                break;

            case RenderAttachmentLoadIntent.ReadOnlyLoad:
                if (!hasRead || hasWrite || state != RenderResourceState.DepthReadAttachment)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' uses read-only load intent for '{resource}' without read-only depth access.");
                }
                break;

            case RenderAttachmentLoadIntent.ClearThenLoad:
                if (!hasRead || !hasWrite)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{passName}' uses ClearThenLoad for attachment '{resource}' without read/write access.");
                }
                break;
        }
    }

    private static bool RequiresExistingAttachmentContent(RenderAttachmentIntent intent)
    {
        return intent.Load is RenderAttachmentLoadIntent.Load or RenderAttachmentLoadIntent.ReadOnlyLoad;
    }

    private static bool IsAttachmentState(RenderResourceState state)
    {
        return state is RenderResourceState.ColorAttachment or
            RenderResourceState.DepthAttachment or
            RenderResourceState.DepthReadAttachment;
    }

    private static string FormatAttachmentIntent(RenderAttachmentIntent intent)
    {
        return intent.IsDeclared ? $"{intent.Load}/{intent.Store}" : "None";
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
