using ArisenEngine.Core.Assets;

namespace ArisenEngine.Rendering;

public sealed class RenderResourceReloadQueue
{
    private readonly object m_Lock = new();
    private readonly HashSet<Guid> m_DirtyGuids = new();

    public void MarkDirty(AssetChangeEvent change)
    {
        MarkDirty(change.Guid);
    }

    public void MarkDirty(Guid assetGuid)
    {
        if (assetGuid == Guid.Empty)
        {
            return;
        }

        lock (m_Lock)
        {
            m_DirtyGuids.Add(assetGuid);
        }
    }

    public Guid[] Drain()
    {
        lock (m_Lock)
        {
            if (m_DirtyGuids.Count == 0)
            {
                return Array.Empty<Guid>();
            }

            var dirtyGuids = new Guid[m_DirtyGuids.Count];
            m_DirtyGuids.CopyTo(dirtyGuids);
            m_DirtyGuids.Clear();
            return dirtyGuids;
        }
    }
}
