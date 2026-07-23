using System.Collections.Generic;
using Arisen.Native.RHI;

namespace ArisenEngine.Rendering;

internal sealed class RenderTargetImageStateTracker
{
    private readonly Dictionary<uint, uint> m_InitializedGenerations = new();

    public bool RequiresInitialization(RHIImageHandle image)
    {
        return image.IsValid &&
               (!m_InitializedGenerations.TryGetValue(image.Index, out uint generation) ||
                generation != image.Generation);
    }

    public void MarkInitialized(RHIImageHandle image)
    {
        if (image.IsValid)
        {
            m_InitializedGenerations[image.Index] = image.Generation;
        }
    }
}
