using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Rendering;

public abstract class RenderPipelineAsset : ISerializationCallbackReceiver
{
    public bool IsDirty { get; set; } = true;

    internal RenderPipeline InternalCreatePipeline()
    {
        try
        {
            return CreatePipeline();
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message);
            throw;
        }
    }


    /// <summary>
    ///   <para>Create a IRenderPipeline specific to this asset.</para>
    /// </summary>
    /// <returns>
    ///   <para>Created pipeline.</para>
    /// </returns>
    protected abstract RenderPipeline CreatePipeline();


    #region Serialization Hook

    protected abstract void BeforeSerialize();
    protected abstract void AfterDeserialize();

    public void OnBeforeSerialize()
    {
        BeforeSerialize();
    }

    public void OnAfterDeserialize()
    {
        AfterDeserialize();
    }

    #endregion
}
