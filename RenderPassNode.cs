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
    // Phase 5: Dependency tracking ports.
    // RenderGraph.AddDependency connects port 0 of passes to force execution order.
    protected RenderPassNode(string name = "RenderPass")
    {
        Name = name;
        AddInputPort("In", typeof(void));
        AddOutputPort("Out", typeof(void));
    }

    /// <summary>
    /// Returns the number of independent CPU recording tasks this pass wants for the frame.
    /// Small passes should keep the default single work item.
    /// </summary>
    internal int GetRenderGraphWorkItemCount(RenderContext context)
    {
        return GetWorkItemCount(context);
    }

    internal RenderPassWorkItem GetRenderGraphWorkItem(RenderContext context, int workItemIndex)
    {
        return GetWorkItem(context, workItemIndex);
    }

    protected virtual int GetWorkItemCount(RenderContext context)
    {
        return 1;
    }

    protected virtual RenderPassWorkItem GetWorkItem(RenderContext context, int workItemIndex)
    {
        return RenderPassWorkItem.Pass(workItemIndex);
    }

    internal void RecordWorkItem(RenderContext context, RHICommandBuffer commandBuffer, RenderPassWorkItem workItem)
    {
        if (!commandBuffer.IsValid)
        {
            throw new InvalidOperationException($"Render pass '{Name}' received an invalid command buffer.");
        }

        var commandList = new RenderCommandList(commandBuffer);
        Record(context, commandList, workItem);
    }

    /// <summary>
    /// RenderPassNode is compiled by RenderGraph and recorded through RecordWorkItem.
    /// </summary>
    public override void Execute()
    {
        throw new InvalidOperationException("RenderPassNode must be executed by RenderGraph.");
    }

    /// <summary>
    /// Specific recording logic for this pass.
    /// Override this to addDrawCalls, bindResources, etc.
    /// </summary>
    protected abstract void Record(RenderContext context, RenderCommandList commandList);

    protected virtual void Record(RenderContext context, RenderCommandList commandList, RenderPassWorkItem workItem)
    {
        Record(context, commandList);
    }
}
