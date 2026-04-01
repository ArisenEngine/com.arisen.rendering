using System;
using System.Collections.Generic;

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
        // TODO: Resource dependency resolution logic
        return this;
    }

    /// <summary>
    /// Declares that this pass writes to a resource.
    /// </summary>
    public RenderGraphBuilder Write(RenderResource resource)
    {
        // TODO: Resource dependency resolution logic
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
