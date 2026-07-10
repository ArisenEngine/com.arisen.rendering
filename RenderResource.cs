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
    ShaderRead,
    TransferRead,
    TransferWrite,
    OutputOwnership
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
