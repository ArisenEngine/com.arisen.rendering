using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

internal sealed class RHITexture2DAllocation : IDisposable
{
    public const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    private RHIDevice m_Device;
    private RHIFactory m_Factory;
    private RHIImageHandle m_Image = RHIImageHandle.Invalid;
    private RHIImageViewHandle m_ImageView = RHIImageViewHandle.Invalid;
    private RHISamplerHandle m_Sampler = RHISamplerHandle.Invalid;
    private uint m_BindlessImageIndex = InvalidBindlessIndex;
    private uint m_BindlessSamplerIndex = InvalidBindlessIndex;
    private bool m_Disposed;

    public uint Width { get; }
    public uint Height { get; }
    public uint MipCount { get; }
    public EFormat Format { get; }
    public RHIImageHandle Image => m_Image;
    public RHIImageViewHandle ImageView => m_ImageView;
    public RHISamplerHandle Sampler => m_Sampler;
    public uint BindlessImageIndex => m_BindlessImageIndex;
    public uint BindlessSamplerIndex => m_BindlessSamplerIndex;
    public bool IsValid => m_Image.IsValid && m_ImageView.IsValid && m_Sampler.IsValid;

    public RHITexture2DAllocation(
        RHIDevice device,
        ReadOnlyMemory<byte> pixelBytes,
        uint width,
        uint height,
        uint mipCount,
        EFormat format,
        MaterialTextureSamplerSettings samplerSettings,
        string name)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException(
                "[RHITexture2DAllocation] Cannot upload a texture with an invalid RHI device.",
                nameof(device));
        }

        if (pixelBytes.IsEmpty || width == 0 || height == 0 || mipCount == 0 ||
            format == EFormat.FORMAT_UNDEFINED)
        {
            throw new ArgumentException(
                "[RHITexture2DAllocation] Texture upload data, dimensions, mip count, and format must be valid.",
                nameof(pixelBytes));
        }
        if (!samplerSettings.IsValid)
        {
            throw new ArgumentException(
                "[RHITexture2DAllocation] Texture sampler settings are invalid.",
                nameof(samplerSettings));
        }

        m_Device = device;
        m_Factory = device.GetFactory();
        Width = width;
        Height = height;
        MipCount = mipCount;
        Format = format;

        try
        {
            Create(pixelBytes, samplerSettings, name);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private unsafe void Create(
        ReadOnlyMemory<byte> pixelBytes,
        MaterialTextureSamplerSettings samplerSettings,
        string name)
    {
        var expectedPixelByteCount = GetPackedMipByteCount(Width, Height, MipCount, Format);
        if (pixelBytes.Length != expectedPixelByteCount)
        {
            throw new ArgumentException(
                $"[RHITexture2DAllocation] Packed mip payload for '{name}' has {pixelBytes.Length} bytes, expected {expectedPixelByteCount}.",
                nameof(pixelBytes));
        }

        var pixelByteCount = checked((ulong)pixelBytes.Length);
        var stagingBuffer = m_Factory.CreateBuffer(
            pixelByteCount,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_SRC_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{name}.TextureUpload");

        try
        {
            var mapped = m_Factory.MapBuffer(stagingBuffer);
            if (mapped == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"[RHITexture2DAllocation] Failed to map upload buffer for '{name}'.");
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
                Width,
                Height,
                1,
                MipCount,
                1,
                Format,
                (uint)EImageUsageFlagBits.IMAGE_USAGE_TRANSFER_DST_BIT |
                (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
                ERHIMemoryUsage.GpuOnly,
                name);
            if (!m_Image.IsValid)
            {
                throw new InvalidOperationException(
                    $"[RHITexture2DAllocation] Failed to create image for '{name}'.");
            }

            UploadToImage(stagingBuffer);

            m_ImageView = m_Factory.CreateImageView(
                m_Image,
                EImageViewType.IMAGE_VIEW_TYPE_2D,
                Format,
                (uint)EImageAspectFlagBits.IMAGE_ASPECT_COLOR_BIT,
                0,
                MipCount,
                0,
                1);
            if (!m_ImageView.IsValid)
            {
                throw new InvalidOperationException(
                    $"[RHITexture2DAllocation] Failed to create image view for '{name}'.");
            }

            RHICapabilities capabilities = m_Device.GetCapabilities();
            float supportedAnisotropy =
                float.IsFinite(capabilities.MaxSamplerAnisotropy) &&
                capabilities.MaxSamplerAnisotropy >= 1.0f
                    ? capabilities.MaxSamplerAnisotropy
                    : 1.0f;
            float maxAnisotropy = Math.Min(
                samplerSettings.MaxAnisotropy,
                supportedAnisotropy);
            m_Sampler = m_Factory.CreateSampler(
                ResolveRhiFilter(samplerSettings.MagFilter),
                ResolveRhiFilter(samplerSettings.MinFilter),
                ResolveRhiMipmapMode(samplerSettings.MipmapMode),
                ResolveRhiAddressMode(samplerSettings.WrapU),
                ResolveRhiAddressMode(samplerSettings.WrapV),
                ResolveRhiAddressMode(samplerSettings.WrapV),
                0.0f,
                MipCount - 1.0f,
                maxAnisotropy);
            if (!m_Sampler.IsValid)
            {
                throw new InvalidOperationException(
                    $"[RHITexture2DAllocation] Failed to create sampler for '{name}'.");
            }

            m_BindlessImageIndex = m_Factory.RegisterBindlessResourceImage(m_ImageView);
            m_BindlessSamplerIndex = m_Factory.RegisterBindlessResourceSampler(m_Sampler);
            if (m_BindlessImageIndex == InvalidBindlessIndex ||
                m_BindlessSamplerIndex == InvalidBindlessIndex)
            {
                throw new InvalidOperationException(
                    $"[RHITexture2DAllocation] Failed to register bindless descriptors for '{name}'.");
            }
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
            ulong bufferOffset = 0;
            var bytesPerPixel = GetFormatBytesPerPixel(Format);
            for (uint mipLevel = 0; mipLevel < MipCount; mipLevel++)
            {
                var mipWidth = Math.Max(1u, Width >> checked((int)mipLevel));
                var mipHeight = Math.Max(1u, Height >> checked((int)mipLevel));
                commandBuffer.CopyBufferToImage2D(
                    stagingBuffer,
                    m_Image,
                    EImageLayout.IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    bufferOffset,
                    mipLevel,
                    mipWidth,
                    mipHeight);
                bufferOffset = checked(bufferOffset +
                    checked((ulong)mipWidth * mipHeight * bytesPerPixel));
            }
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

    private static EFilter ResolveRhiFilter(MaterialTextureFilter filter)
    {
        return filter switch
        {
            MaterialTextureFilter.Nearest => EFilter.FILTER_NEAREST,
            MaterialTextureFilter.Linear => EFilter.FILTER_LINEAR,
            _ => throw new NotSupportedException(
                $"Texture filter '{filter}' is not supported by the RHI texture uploader.")
        };
    }

    private static ESamplerMipmapMode ResolveRhiMipmapMode(MaterialTextureMipmapMode mipmapMode)
    {
        return mipmapMode switch
        {
            MaterialTextureMipmapMode.Nearest => ESamplerMipmapMode.SAMPLER_MIPMAP_MODE_NEAREST,
            MaterialTextureMipmapMode.Linear => ESamplerMipmapMode.SAMPLER_MIPMAP_MODE_LINEAR,
            _ => throw new NotSupportedException(
                $"Texture mipmap mode '{mipmapMode}' is not supported by the RHI texture uploader.")
        };
    }

    private static ESamplerAddressMode ResolveRhiAddressMode(MaterialTextureWrapMode wrapMode)
    {
        return wrapMode switch
        {
            MaterialTextureWrapMode.Repeat => ESamplerAddressMode.SAMPLER_ADDRESS_MODE_REPEAT,
            MaterialTextureWrapMode.MirroredRepeat => ESamplerAddressMode.SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT,
            MaterialTextureWrapMode.ClampToEdge => ESamplerAddressMode.SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            _ => throw new NotSupportedException(
                $"Texture wrap mode '{wrapMode}' is not supported by the RHI texture uploader.")
        };
    }

    private static int GetPackedMipByteCount(
        uint width,
        uint height,
        uint mipCount,
        EFormat format)
    {
        var bytesPerPixel = GetFormatBytesPerPixel(format);
        ulong byteCount = 0;
        for (uint mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            var mipWidth = Math.Max(1u, width >> checked((int)mipLevel));
            var mipHeight = Math.Max(1u, height >> checked((int)mipLevel));
            byteCount = checked(byteCount + checked((ulong)mipWidth * mipHeight * bytesPerPixel));
        }

        return checked((int)byteCount);
    }

    private static uint GetFormatBytesPerPixel(EFormat format)
    {
        return format switch
        {
            EFormat.FORMAT_R8G8B8A8_UNORM => 4,
            EFormat.FORMAT_R8G8B8A8_SRGB => 4,
            EFormat.FORMAT_R16G16_SFLOAT => 4,
            EFormat.FORMAT_R16G16B16A16_SFLOAT => 8,
            _ => throw new NotSupportedException(
                $"Texture format '{format}' has no packed upload byte-size definition.")
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

        m_Sampler = RHISamplerHandle.Invalid;
        m_ImageView = RHIImageViewHandle.Invalid;
        m_Image = RHIImageHandle.Invalid;
        m_BindlessImageIndex = InvalidBindlessIndex;
        m_BindlessSamplerIndex = InvalidBindlessIndex;
        m_Disposed = true;
    }
}
