using ArisenEngine.Threading;
using ArisenEngine.Core.RHI;
using System;

namespace ArisenEngine.Rendering;

/// <summary>
/// A specialized TaskNode for recording rendering commands.
/// This allows the RenderGraph to record passes in parallel using the TaskGraph.
/// </summary>
public abstract class RenderPassNode : TaskNode
{
    private RenderContext m_Context;
    private RHICommandBuffer? m_CommandBuffer;

    public RHICommandBuffer? CommandBuffer => m_CommandBuffer;

    // Phase 5: Dependency tracking ports. 
    // RenderGraph.AddDependency connects port 0 of passes to force execution order.
    protected RenderPassNode(string name = "RenderPass")
    {
        Name = name;
        AddInputPort("In", typeof(void));
        AddOutputPort("Out", typeof(void));
    }

    /// <summary>
    /// Initialized by the RenderGraph before execution.
    /// </summary>
    public void Setup(RenderContext context, RHICommandBuffer commandBuffer)
    {
        m_Context = context;
        m_CommandBuffer = commandBuffer;
    }

    /// <summary>
    /// Implements the execution by calling the specific RenderPass recording logic.
    /// </summary>
    public override void Execute()
    {
        if (m_CommandBuffer == null)
            throw new InvalidOperationException("RenderPassNode executed without a valid CommandBuffer.");

        // Record the pass logic
        Record(m_Context, m_CommandBuffer.Value);
    }

    /// <summary>
    /// Specific recording logic for this pass.
    /// Override this to addDrawCalls, bindResources, etc.
    /// </summary>
    protected abstract void Record(RenderContext context, RHICommandBuffer commandBuffer);
}
