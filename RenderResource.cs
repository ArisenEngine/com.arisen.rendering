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

public sealed class RenderResource
{
    public string Name { get; }
    public RenderResourceType Type { get; }
    public uint ResourceId { get; }

    internal RenderResource(string name, RenderResourceType type, uint resourceId)
    {
        Name = name;
        Type = type;
        ResourceId = resourceId;
    }
}
