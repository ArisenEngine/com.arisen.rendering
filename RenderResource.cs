using System;

namespace ArisenEngine.Rendering;

/// <summary>
/// A managed resource (Texture or Buffer) inside the RenderGraph.
/// Handles lifetime tracking and synchronization.
/// </summary>
public enum RenderResourceType
{
    Texture,
    Buffer
}

public enum RenderResourceState
{
    Unknown,
    ColorAttachment,
    DepthAttachment,
    DepthReadAttachment,
    ShaderRead,
    TransferRead,
    TransferWrite,
    OutputOwnership
}

public enum RenderAttachmentLoadIntent : byte
{
    None,
    DontCare,
    Clear,
    Load,
    ReadOnlyLoad,
    ClearThenLoad
}

public enum RenderAttachmentStoreIntent : byte
{
    None,
    Store,
    Discard
}

public readonly record struct RenderAttachmentIntent(
    RenderAttachmentLoadIntent Load,
    RenderAttachmentStoreIntent Store)
{
    public static RenderAttachmentIntent ClearStore { get; } = new(
        RenderAttachmentLoadIntent.Clear,
        RenderAttachmentStoreIntent.Store);

    public static RenderAttachmentIntent LoadStore { get; } = new(
        RenderAttachmentLoadIntent.Load,
        RenderAttachmentStoreIntent.Store);

    public static RenderAttachmentIntent ReadOnlyLoadStore { get; } = new(
        RenderAttachmentLoadIntent.ReadOnlyLoad,
        RenderAttachmentStoreIntent.Store);

    public static RenderAttachmentIntent ClearThenLoadStore { get; } = new(
        RenderAttachmentLoadIntent.ClearThenLoad,
        RenderAttachmentStoreIntent.Store);

    public bool IsDeclared =>
        Load != RenderAttachmentLoadIntent.None ||
        Store != RenderAttachmentStoreIntent.None;
}

public sealed class RenderResource
{
    public string Name { get; }
    public RenderResourceType Type { get; }
    public uint ResourceId { get; }
    public bool IsImported { get; }
    public RenderResourceState InitialState { get; }

    internal RenderResource(
        string name,
        RenderResourceType type,
        uint resourceId,
        bool isImported = false,
        RenderResourceState initialState = RenderResourceState.Unknown)
    {
        Name = name;
        Type = type;
        ResourceId = resourceId;
        IsImported = isImported;
        InitialState = initialState;
    }

    public override string ToString() => $"{Name}#{ResourceId}({Type})";
}
