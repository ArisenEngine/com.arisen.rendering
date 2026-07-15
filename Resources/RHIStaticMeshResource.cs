using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using System.Numerics;

namespace ArisenEngine.Rendering.Resources;

public sealed class RHIStaticMeshResource : IDisposable
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly MeshAsset m_Asset;
    private RHIDevice m_Device;
    private RHIFactory m_Factory;
    private CookedMesh m_CookedMesh;
    private MeshSubmesh[] m_Submeshes = Array.Empty<MeshSubmesh>();
    private RHIBufferHandle m_VertexBuffer = RHIBufferHandle.Invalid;
    private RHIBufferHandle m_IndexBuffer = RHIBufferHandle.Invalid;
    private bool m_Disposed;

    public RHIBufferHandle VertexBuffer => m_VertexBuffer;
    public RHIBufferHandle IndexBuffer => m_IndexBuffer;
    public uint VertexCount => m_CookedMesh.VertexCount;
    public uint VertexStride => m_CookedMesh.VertexStride;
    public uint IndexCount => m_CookedMesh.IndexCount;
    public EIndexType IndexType => ResolveIndexType(m_CookedMesh.IndexFormat);
    public MeshBounds Bounds => m_CookedMesh.Bounds;
    public int SubmeshCount => m_Submeshes.Length;
    public ReadOnlySpan<MeshSubmesh> Submeshes => m_Submeshes;
    public AssetDependencyStamp DependencyStamp { get; private set; }
    public bool IsValid => m_VertexBuffer.IsValid && m_IndexBuffer.IsValid && IndexCount > 0;

    public RHIStaticMeshResource(RHIDevice device, IAssetDatabase assetDatabase, MeshAsset asset)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException("[RHIStaticMeshResource] Cannot create a mesh with an invalid RHI device.", nameof(device));
        }

        m_Device = device;
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
        DependencyStamp = AssetDependencyTracker.GetAssetStamp(m_AssetDatabase, m_Asset.Guid);
        m_CookedMesh = MeshAssetCooker.LoadOrCook(m_AssetDatabase, m_Asset);
        var cookedBytes = m_AssetDatabase.GetCookedAssetBytes(m_CookedMesh.Handle);
        var vertexBytes = cookedBytes.Slice(
            checked((int)m_CookedMesh.VertexDataOffset),
            checked((int)m_CookedMesh.VertexDataSize));
        var indexBytes = cookedBytes.Slice(
            checked((int)m_CookedMesh.IndexDataOffset),
            checked((int)m_CookedMesh.IndexDataSize));
        m_Submeshes = new MeshSubmesh[checked((int)m_CookedMesh.SubmeshCount)];
        MeshAssetCooker.ReadSubmeshes(cookedBytes.Span, m_CookedMesh, m_Submeshes);

        var vertexStagingBuffer = m_Factory.CreateBuffer(
            m_CookedMesh.VertexDataSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_SRC_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{m_Asset.Name}.VertexUpload");

        var indexStagingBuffer = m_Factory.CreateBuffer(
            m_CookedMesh.IndexDataSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_SRC_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{m_Asset.Name}.IndexUpload");

        try
        {
            if (!vertexStagingBuffer.IsValid)
            {
                throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to create vertex staging buffer for '{m_Asset.Name}'.");
            }

            if (!indexStagingBuffer.IsValid)
            {
                throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to create index staging buffer for '{m_Asset.Name}'.");
            }

            CopyPayloadToBuffer(vertexStagingBuffer, vertexBytes, m_Asset.Name);
            CopyPayloadToBuffer(indexStagingBuffer, indexBytes, m_Asset.Name);

            m_VertexBuffer = m_Factory.CreateBuffer(
                m_CookedMesh.VertexDataSize,
                (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_DST_BIT |
                (uint)EBufferUsageFlagBits.BUFFER_USAGE_VERTEX_BUFFER_BIT,
                ESharingMode.SHARING_MODE_EXCLUSIVE,
                ERHIMemoryUsage.GpuOnly,
                $"{m_Asset.Name}.VertexBuffer");

            if (!m_VertexBuffer.IsValid)
            {
                throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to create device-local vertex buffer for '{m_Asset.Name}'.");
            }

            m_IndexBuffer = m_Factory.CreateBuffer(
                m_CookedMesh.IndexDataSize,
                (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_DST_BIT |
                (uint)EBufferUsageFlagBits.BUFFER_USAGE_INDEX_BUFFER_BIT,
                ESharingMode.SHARING_MODE_EXCLUSIVE,
                ERHIMemoryUsage.GpuOnly,
                $"{m_Asset.Name}.IndexBuffer");

            if (!m_IndexBuffer.IsValid)
            {
                throw new InvalidOperationException($"[RHIStaticMeshResource] Failed to create device-local index buffer for '{m_Asset.Name}'.");
            }

            UploadToDeviceLocalBuffers(vertexStagingBuffer, indexStagingBuffer);
        }
        finally
        {
            if (indexStagingBuffer.IsValid)
            {
                m_Factory.ReleaseBuffer(indexStagingBuffer);
            }

            if (vertexStagingBuffer.IsValid)
            {
                m_Factory.ReleaseBuffer(vertexStagingBuffer);
            }
        }

        Logger.Log(
            $"[RHIStaticMeshResource] Uploaded device-local mesh | Name: {m_Asset.Name} | Vertices: {VertexCount} | Indices: {IndexCount} | Submeshes: {SubmeshCount} | Bounds: {Bounds.Min}->{Bounds.Max} | VertexStride: {VertexStride} | VertexBuffer: {m_VertexBuffer.Index}:{m_VertexBuffer.Generation} | IndexBuffer: {m_IndexBuffer.Index}:{m_IndexBuffer.Generation}");
    }

    public MeshSubmesh GetSubmeshOrDefault(int submeshIndex)
    {
        if ((uint)submeshIndex < (uint)m_Submeshes.Length)
        {
            return m_Submeshes[submeshIndex];
        }

        return new MeshSubmesh(0, IndexCount, 0, 0);
    }

    public MeshDrawCommand CreateDrawCommand(Matrix4x4 localToWorld, uint materialId, int submeshIndex = 0)
    {
        var submesh = GetSubmeshOrDefault(submeshIndex);
        return CreateDrawCommand(localToWorld, materialId, submesh, applyMaterialSlot: true);
    }

    public MeshDrawCommand CreateDrawCommandWithMaterialOverride(Matrix4x4 localToWorld, uint materialId, int submeshIndex = 0)
    {
        var submesh = GetSubmeshOrDefault(submeshIndex);
        return CreateDrawCommand(localToWorld, materialId, submesh, applyMaterialSlot: false);
    }

    private MeshDrawCommand CreateDrawCommand(
        Matrix4x4 localToWorld,
        uint materialId,
        MeshSubmesh submesh,
        bool applyMaterialSlot)
    {
        return new MeshDrawCommand
        {
            LocalToWorld = localToWorld,
            VertexBuffer = VertexBuffer,
            IndexBuffer = IndexBuffer,
            FirstIndex = submesh.FirstIndex,
            IndexCount = submesh.IndexCount,
            VertexOffset = submesh.VertexOffset,
            IndexType = IndexType,
            MaterialID = applyMaterialSlot
                ? checked(materialId + submesh.MaterialSlot)
                : materialId
        };
    }

    public int CreateDrawCommands(
        Span<MeshDrawCommand> destination,
        Matrix4x4 localToWorld,
        uint materialId,
        int firstSubmeshIndex = 0,
        int submeshCount = -1)
    {
        return CreateDrawCommands(
            destination,
            localToWorld,
            materialId,
            firstSubmeshIndex,
            submeshCount,
            applyMaterialSlots: true);
    }

    public int CreateDrawCommandsWithMaterialOverride(
        Span<MeshDrawCommand> destination,
        Matrix4x4 localToWorld,
        uint materialId,
        int firstSubmeshIndex = 0,
        int submeshCount = -1)
    {
        return CreateDrawCommands(
            destination,
            localToWorld,
            materialId,
            firstSubmeshIndex,
            submeshCount,
            applyMaterialSlots: false);
    }

    private int CreateDrawCommands(
        Span<MeshDrawCommand> destination,
        Matrix4x4 localToWorld,
        uint materialId,
        int firstSubmeshIndex,
        int submeshCount,
        bool applyMaterialSlots)
    {
        if (firstSubmeshIndex < 0 || firstSubmeshIndex > m_Submeshes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSubmeshIndex));
        }

        if (submeshCount < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(submeshCount));
        }

        int availableSubmeshes = m_Submeshes.Length - firstSubmeshIndex;
        int drawCount = submeshCount < 0
            ? availableSubmeshes
            : Math.Min(submeshCount, availableSubmeshes);

        if (destination.Length < drawCount)
        {
            throw new ArgumentException("[RHIStaticMeshResource] Destination span is smaller than the requested submesh draw count.", nameof(destination));
        }

        for (int i = 0; i < drawCount; i++)
        {
            destination[i] = CreateDrawCommand(
                localToWorld,
                materialId,
                m_Submeshes[firstSubmeshIndex + i],
                applyMaterialSlots);
        }

        return drawCount;
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

    private void UploadToDeviceLocalBuffers(RHIBufferHandle vertexStagingBuffer, RHIBufferHandle indexStagingBuffer)
    {
        var commandPool = m_Factory.CreateCommandBufferPool(RHIQueueType.Graphics);
        RHICommandBuffer commandBuffer = default;

        try
        {
            commandBuffer = commandPool.GetCommandBuffer(0);
            commandBuffer.Begin();
            commandBuffer.CopyBuffer(
                vertexStagingBuffer,
                0,
                m_VertexBuffer,
                0,
                m_CookedMesh.VertexDataSize);
            commandBuffer.CopyBuffer(
                indexStagingBuffer,
                0,
                m_IndexBuffer,
                0,
                m_CookedMesh.IndexDataSize);

            Span<RHIBufferMemoryBarrier> barriers = stackalloc RHIBufferMemoryBarrier[2];
            barriers[0] = CreateBufferBarrier(
                m_VertexBuffer,
                EAccessFlag.ACCESS_VERTEX_ATTRIBUTE_READ_BIT);
            barriers[1] = CreateBufferBarrier(
                m_IndexBuffer,
                EAccessFlag.ACCESS_INDEX_READ_BIT);

            commandBuffer.PipelineBarrier(
                EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT,
                EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_INPUT_BIT,
                barriers);
            commandBuffer.End();

            var ticket = m_Device.Submit(commandBuffer);
            m_Device.WaitQueueTicket(ticket);
        }
        finally
        {
            if (commandBuffer.IsValid)
            {
                commandPool.ReleaseCommandBuffer(0, commandBuffer.RHIHandle);
            }

            if (commandPool.IsValid)
            {
                m_Factory.ReleaseCommandBufferPool(commandPool.RHIHandle);
            }
        }
    }

    private static RHIBufferMemoryBarrier CreateBufferBarrier(RHIBufferHandle buffer, EAccessFlag dstAccess)
    {
        return new RHIBufferMemoryBarrier
        {
            SrcAccessMask = EAccessFlag.ACCESS_TRANSFER_WRITE_BIT,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = RHIQueueFamily.Ignored,
            DstQueueFamilyIndex = RHIQueueFamily.Ignored,
            Buffer = buffer,
            SrcStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT,
            DstStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_INPUT_BIT
        };
    }

    private static EIndexType ResolveIndexType(MeshIndexFormat indexFormat)
    {
        return indexFormat switch
        {
            MeshIndexFormat.UInt32 => EIndexType.INDEX_TYPE_UINT32,
            _ => throw new NotSupportedException($"Mesh index format '{indexFormat}' is not supported by the RHI mesh resource.")
        };
    }

    public bool IsSourceStale()
    {
        return AssetDependencyTracker.GetAssetStamp(m_AssetDatabase, m_Asset.Guid) != DependencyStamp;
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
        m_Submeshes = Array.Empty<MeshSubmesh>();
        m_Disposed = true;
    }
}
