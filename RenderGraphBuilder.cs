using System;

namespace ArisenEngine.Rendering;

/// <summary>
/// A helper class to fluidly build the RenderGraph passes.
/// </summary>
public sealed class RenderGraphBuilder
{
    private readonly RenderGraph m_Graph;
    private readonly RenderPassNode m_Pass;

    internal RenderGraphBuilder(RenderGraph graph, RenderPassNode pass)
    {
        m_Graph = graph;
        m_Pass = pass;
    }

    /// <summary>
    /// Declares that this pass reads from a resource.
    /// This will automatically add a dependency on any pass that writes to this resource.
    /// </summary>
    public RenderGraphBuilder Read(RenderResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        m_Graph.RegisterRead(m_Pass, resource, RenderResourceState.ShaderRead);
        return this;
    }

    /// <summary>
    /// Declares that this pass writes to a resource.
    /// </summary>
    public RenderGraphBuilder Write(RenderResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        m_Graph.RegisterWrite(m_Pass, resource, RenderResourceState.TransferWrite);
        return this;
    }

    private RenderGraphBuilder Read(
        RenderResource resource,
        RenderResourceState state,
        RenderAttachmentIntent attachmentIntent = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        m_Graph.RegisterRead(m_Pass, resource, state, attachmentIntent);
        return this;
    }

    private RenderGraphBuilder Write(
        RenderResource resource,
        RenderResourceState state,
        RenderAttachmentIntent attachmentIntent = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        m_Graph.RegisterWrite(m_Pass, resource, state, attachmentIntent);
        return this;
    }

    public RenderGraphBuilder WriteColorAttachment(
        RenderResource resource,
        RenderAttachmentIntent attachmentIntent)
    {
        return Write(resource, RenderResourceState.ColorAttachment, attachmentIntent);
    }

    public RenderGraphBuilder ReadWriteColorAttachment(
        RenderResource resource,
        RenderAttachmentIntent attachmentIntent)
    {
        Read(resource, RenderResourceState.ColorAttachment, attachmentIntent);
        return Write(resource, RenderResourceState.ColorAttachment, attachmentIntent);
    }

    public RenderGraphBuilder ReadDepthAttachment(
        RenderResource resource,
        RenderAttachmentIntent attachmentIntent)
    {
        return Read(resource, RenderResourceState.DepthReadAttachment, attachmentIntent);
    }

    public RenderGraphBuilder WriteDepthAttachment(
        RenderResource resource,
        RenderAttachmentIntent attachmentIntent)
    {
        return Write(resource, RenderResourceState.DepthAttachment, attachmentIntent);
    }

    public RenderGraphBuilder ReadWriteDepthAttachment(
        RenderResource resource,
        RenderAttachmentIntent attachmentIntent)
    {
        Read(resource, RenderResourceState.DepthAttachment, attachmentIntent);
        return Write(resource, RenderResourceState.DepthAttachment, attachmentIntent);
    }

    public RenderGraphBuilder ReadShader(RenderResource resource)
    {
        return Read(resource, RenderResourceState.ShaderRead);
    }

    public RenderGraphBuilder ReadTransfer(RenderResource resource)
    {
        return Read(resource, RenderResourceState.TransferRead);
    }

    public RenderGraphBuilder WriteTransfer(RenderResource resource)
    {
        return Write(resource, RenderResourceState.TransferWrite);
    }

    public RenderGraphBuilder OwnOutput(RenderResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        m_Graph.RegisterWrite(m_Pass, resource, RenderResourceState.OutputOwnership);
        return this;
    }

    /// <summary>
    /// Manually adds a dependency on another pass.
    /// </summary>
    public RenderGraphBuilder DependsOn(RenderPassNode src)
    {
        m_Graph.AddDependency(src, m_Pass);
        return this;
    }
}
