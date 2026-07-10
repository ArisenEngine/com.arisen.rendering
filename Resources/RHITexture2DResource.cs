using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

public sealed class RHITexture2DResource : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Texture2DAsset m_Asset;
    private RHIDevice m_Device;
    private RHIFactory m_Factory;
    private CookedTexture2D m_CookedTexture;
    private RHIImageHandle m_Image = RHIImageHandle.Invalid;
    private RHIImageViewHandle m_ImageView = RHIImageViewHandle.Invalid;
    private RHISamplerHandle m_Sampler = RHISamplerHandle.Invalid;
    private uint m_BindlessImageIndex = InvalidBindlessIndex;
    private uint m_BindlessSamplerIndex = InvalidBindlessIndex;
    private bool m_Disposed;

    public uint Width => m_CookedTexture.Width;
    public uint Height => m_CookedTexture.Height;
    public EFormat Format { get; private set; } = EFormat.FORMAT_UNDEFINED;
    public RHIImageHandle Image => m_Image;
    public RHIImageViewHandle ImageView => m_ImageView;
    public RHISamplerHandle Sampler => m_Sampler;
    public uint BindlessImageIndex => m_BindlessImageIndex;
    public uint BindlessSamplerIndex => m_BindlessSamplerIndex;
    public AssetDependencyStamp DependencyStamp { get; private set; }
    public bool IsValid => m_Image.IsValid && m_ImageView.IsValid && m_Sampler.IsValid;

    public RHITexture2DResource(RHIDevice device, IAssetDatabase assetDatabase, Texture2DAsset asset)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException("[RHITexture2DResource] Cannot create a texture with an invalid RHI device.", nameof(device));
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
        m_CookedTexture = Texture2DAssetCooker.LoadOrCook(m_AssetDatabase, m_Asset);
        var cookedBytes = m_AssetDatabase.GetCookedAssetBytes(m_CookedTexture.Handle);
        var pixelBytes = cookedBytes.Slice(m_CookedTexture.PixelDataOffset, m_CookedTexture.PixelDataSize);

        Format = ResolveRhiFormat(m_CookedTexture);
        var pixelByteCount = checked((ulong)pixelBytes.Length);

        var stagingBuffer = m_Factory.CreateBuffer(
            pixelByteCount,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_SRC_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{m_Asset.Name}.TextureUpload");

        try
        {
            var mapped = m_Factory.MapBuffer(stagingBuffer);
            if (mapped == IntPtr.Zero)
            {
                throw new InvalidOperationException($"[RHITexture2DResource] Failed to map upload buffer for '{m_Asset.Name}'.");
            }

            try
            {
                pixelBytes.Span.CopyTo(new Span<byte>(mapped.ToPointer(), pixelBytes.Length));
            }
            finally
            {
                m_Factory.UnmapBuffer(stagingBuffer);
            }

            m_Image = m_Factory.CreateImage(
                m_CookedTexture.Width,
                m_CookedTexture.Height,
                1,
                checked((uint)m_CookedTexture.MipCount),
                1,
                Format,
                (uint)EImageUsageFlagBits.IMAGE_USAGE_TRANSFER_DST_BIT |
                (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
                ERHIMemoryUsage.GpuOnly,
                m_Asset.Name);

            if (!m_Image.IsValid)
            {
                throw new InvalidOperationException($"[RHITexture2DResource] Failed to create image for '{m_Asset.Name}'.");
            }

            UploadToImage(stagingBuffer);

            m_ImageView = m_Factory.CreateImageView(
                m_Image,
                EImageViewType.IMAGE_VIEW_TYPE_2D,
                Format,
                (uint)EImageAspectFlagBits.IMAGE_ASPECT_COLOR_BIT,
                0,
                checked((uint)m_CookedTexture.MipCount),
                0,
                1);

            if (!m_ImageView.IsValid)
            {
                throw new InvalidOperationException($"[RHITexture2DResource] Failed to create image view for '{m_Asset.Name}'.");
            }

            m_Sampler = m_Factory.CreateSampler(
                EFilter.FILTER_LINEAR,
                EFilter.FILTER_LINEAR,
                ESamplerMipmapMode.SAMPLER_MIPMAP_MODE_NEAREST,
                ESamplerAddressMode.SAMPLER_ADDRESS_MODE_REPEAT);

            if (!m_Sampler.IsValid)
            {
                throw new InvalidOperationException($"[RHITexture2DResource] Failed to create sampler for '{m_Asset.Name}'.");
            }

            m_BindlessImageIndex = m_Factory.RegisterBindlessResourceImage(m_ImageView);
            m_BindlessSamplerIndex = m_Factory.RegisterBindlessResourceSampler(m_Sampler);
            if (m_BindlessImageIndex == InvalidBindlessIndex || m_BindlessSamplerIndex == InvalidBindlessIndex)
            {
                throw new InvalidOperationException($"[RHITexture2DResource] Failed to register bindless descriptors for '{m_Asset.Name}'.");
            }

            Logger.Log(
                $"[RHITexture2DResource] Uploaded texture | Name: {m_Asset.Name} | Size: {Width}x{Height} | Format: {Format} | Image: {m_Image.Index}:{m_Image.Generation} | View: {m_ImageView.Index}:{m_ImageView.Generation} | BindlessImage: {m_BindlessImageIndex} | BindlessSampler: {m_BindlessSamplerIndex}");
        }
        finally
        {
            if (stagingBuffer.IsValid)
            {
                m_Factory.ReleaseBuffer(stagingBuffer);
            }
        }
    }

    private void UploadToImage(RHIBufferHandle stagingBuffer)
    {
        var commandPool = m_Factory.CreateCommandBufferPool(RHIQueueType.Graphics);
        RHICommandBuffer commandBuffer = default;

        try
        {
            commandBuffer = commandPool.GetCommandBuffer(0);
            commandBuffer.Begin();
            commandBuffer.TransitionImageLayout(
                m_Image,
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EImageLayout.IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
            commandBuffer.CopyBufferToImage2D(
                stagingBuffer,
                m_Image,
                EImageLayout.IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                0,
                m_CookedTexture.Width,
                m_CookedTexture.Height);
            commandBuffer.TransitionImageLayout(
                m_Image,
                EImageLayout.IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                EImageLayout.IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
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

    private static EFormat ResolveRhiFormat(CookedTexture2D texture)
    {
        if (texture.Format != Texture2DCookedFormat.R8G8B8A8UNorm)
        {
            throw new NotSupportedException($"Texture format '{texture.Format}' is not supported by the RHI texture uploader yet.");
        }

        return texture.ColorSpace == Texture2DColorSpace.SRgb
            ? EFormat.FORMAT_R8G8B8A8_SRGB
            : EFormat.FORMAT_R8G8B8A8_UNORM;
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
            if (m_BindlessSamplerIndex != InvalidBindlessIndex)
            {
                m_Factory.UnregisterBindlessResourceSampler(m_BindlessSamplerIndex);
            }

            if (m_BindlessImageIndex != InvalidBindlessIndex)
            {
                m_Factory.UnregisterBindlessResourceImage(m_BindlessImageIndex);
            }

            if (m_Sampler.IsValid)
            {
                m_Factory.ReleaseSampler(m_Sampler);
            }

            if (m_ImageView.IsValid)
            {
                m_Factory.ReleaseImageView(m_ImageView);
            }

            if (m_Image.IsValid)
            {
                m_Factory.ReleaseImage(m_Image);
            }
        }

        if (m_CookedTexture.IsValid)
        {
            m_AssetDatabase.Release(m_CookedTexture.Handle);
        }

        m_Sampler = RHISamplerHandle.Invalid;
        m_ImageView = RHIImageViewHandle.Invalid;
        m_Image = RHIImageHandle.Invalid;
        m_CookedTexture = default;
        m_BindlessImageIndex = InvalidBindlessIndex;
        m_BindlessSamplerIndex = InvalidBindlessIndex;
        m_Disposed = true;
    }
}
