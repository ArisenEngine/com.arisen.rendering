using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

public sealed class RHIStaticMeshResource : IDisposable
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly MeshAsset m_Asset;
    private RHIFactory m_Factory;
    private CookedMesh m_CookedMesh;
    private RHIBufferHandle m_VertexBuffer = RHIBufferHandle.Invalid;
    private RHIBufferHandle m_IndexBuffer = RHIBufferHandle.Invalid;
    private bool m_Disposed;

    public RHIBufferHandle VertexBuffer => m_VertexBuffer;
    public RHIBufferHandle IndexBuffer => m_IndexBuffer;
    public uint VertexCount => m_CookedMesh.VertexCount;
    public uint VertexStride => m_CookedMesh.VertexStride;
    public uint IndexCount => m_CookedMesh.IndexCount;
    public EIndexType IndexType => ResolveIndexType(m_CookedMesh.IndexFormat);
    public bool IsValid => m_VertexBuffer.IsValid && m_IndexBuffer.IsValid && IndexCount > 0;

    public RHIStaticMeshResource(RHIDevice device, IAssetDatabase assetDatabase, MeshAsset asset)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException("[RHIStaticMeshResource] Cannot create a mesh with an invalid RHI device.", nameof(device));
        }

        m_Factory = device.GetFactory();
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Asset = asset ?? throw new ArgumentNullException(nameof(asset));

        try
        {
            CreateFromCookedAsset();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private unsafe void CreateFromCookedAsset()
    {
        m_CookedMesh = MeshAssetCooker.LoadOrCook(m_AssetDatabase, m_Asset);
        var cookedBytes = m_AssetDatabase.GetCookedAssetBytes(m_CookedMesh.Handle);
        var vertexBytes = cookedBytes.Slice(
            checked((int)m_CookedMesh.VertexDataOffset),
            checked((int)m_CookedMesh.VertexDataSize));
        var indexBytes = cookedBytes.Slice(
            checked((int)m_CookedMesh.IndexDataOffset),
            checked((int)m_CookedMesh.IndexDataSize));

        m_VertexBuffer = m_Factory.CreateBuffer(
            m_CookedMesh.VertexDataSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_VERTEX_BUFFER_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{m_Asset.Name}.VertexBuffer");

        if (!m_VertexBuffer.IsValid)
        {
            throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to create vertex buffer for '{m_Asset.Name}'.");
        }

        m_IndexBuffer = m_Factory.CreateBuffer(
            m_CookedMesh.IndexDataSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_INDEX_BUFFER_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{m_Asset.Name}.IndexBuffer");

        if (!m_IndexBuffer.IsValid)
        {
            throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to create index buffer for '{m_Asset.Name}'.");
        }

        CopyPayloadToBuffer(m_VertexBuffer, vertexBytes, m_Asset.Name);
        CopyPayloadToBuffer(m_IndexBuffer, indexBytes, m_Asset.Name);

        Logger.Log(
            $"[RHIStaticMeshResource] Uploaded mesh | Name: {m_Asset.Name} | Vertices: {VertexCount} | Indices: {IndexCount} | VertexStride: {VertexStride} | VertexBuffer: {m_VertexBuffer.Index}:{m_VertexBuffer.Generation} | IndexBuffer: {m_IndexBuffer.Index}:{m_IndexBuffer.Generation}");
    }

    private unsafe void CopyPayloadToBuffer(RHIBufferHandle buffer, ReadOnlyMemory<byte> payload, string assetName)
    {
        var mapped = m_Factory.MapBuffer(buffer);
        if (mapped == IntPtr.Zero)
        {
            throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to map buffer for '{assetName}'.");
        }

        try
        {
            payload.Span.CopyTo(new Span<byte>(mapped.ToPointer(), payload.Length));
        }
        finally
        {
            m_Factory.UnmapBuffer(buffer);
        }
    }

    private static EIndexType ResolveIndexType(MeshIndexFormat indexFormat)
    {
        return indexFormat switch
        {
            MeshIndexFormat.UInt32 => EIndexType.INDEX_TYPE_UINT32,
            _ => throw new NotSupportedException($"Mesh index format '{indexFormat}' is not supported by the RHI mesh resource.")
        };
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        if (m_Factory.IsValid)
        {
            if (m_IndexBuffer.IsValid)
            {
                m_Factory.ReleaseBuffer(m_IndexBuffer);
            }

            if (m_VertexBuffer.IsValid)
            {
                m_Factory.ReleaseBuffer(m_VertexBuffer);
            }
        }

        if (m_CookedMesh.IsValid)
        {
            m_AssetDatabase.Release(m_CookedMesh.Handle);
        }

        m_IndexBuffer = RHIBufferHandle.Invalid;
        m_VertexBuffer = RHIBufferHandle.Invalid;
        m_CookedMesh = default;
        m_Disposed = true;
    }
}
