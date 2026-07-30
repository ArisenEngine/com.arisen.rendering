using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

public sealed class RenderGraphTexture : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const uint MaximumArrayLayers = 256;

    private RenderGraphTextureAllocation? m_Allocation;
    private RenderGraphTextureDescriptor m_Descriptor;
    private bool m_Disposed;

    internal RenderGraphTexture(RenderResource resource)
    {
        Resource = resource;
    }

    public RenderResource Resource { get; internal set; }
    public RHIImageHandle Image => m_Allocation?.Image ?? RHIImageHandle.Invalid;
    public RHIImageViewHandle ImageView => m_Allocation?.ImageView ?? RHIImageViewHandle.Invalid;
    public RHISamplerHandle Sampler => m_Allocation?.Sampler ?? RHISamplerHandle.Invalid;
    public uint BindlessImageIndex => m_Allocation?.BindlessImageIndex ?? InvalidBindlessIndex;
    public uint BindlessSamplerIndex => m_Allocation?.BindlessSamplerIndex ?? InvalidBindlessIndex;
    public uint Width => m_Descriptor.Width;
    public uint Height => m_Descriptor.Height;
    public uint ArrayLayers => m_Descriptor.ArrayLayers;
    public EFormat Format => m_Descriptor.Format;
    public uint Usage => m_Descriptor.Usage;
    public EImageAspectFlagBits AspectMask => m_Descriptor.AspectMask;
    public bool IsValid => m_Allocation is { IsValid: true };

    public RHIImageViewHandle GetLayerImageView(uint arrayLayer)
    {
        ThrowIfDisposed();
        ValidateArrayLayer(arrayLayer);
        return m_Allocation?.GetLayerImageView(arrayLayer) ?? RHIImageViewHandle.Invalid;
    }

    public uint GetLayerBindlessImageIndex(uint arrayLayer)
    {
        ThrowIfDisposed();
        ValidateArrayLayer(arrayLayer);
        return m_Allocation?.GetLayerBindlessImageIndex(arrayLayer) ?? InvalidBindlessIndex;
    }

    internal RenderResourceState CurrentState { get; set; } = RenderResourceState.Unknown;

    internal void Ensure(
        RHIFactory factory,
        RenderGraphTextureDescriptor descriptor,
        DeferredRenderResourceDisposalQueue? disposalQueue,
        ulong lastSubmittedTicket)
    {
        ThrowIfDisposed();

        if (!factory.IsValid)
        {
            throw new ArgumentException("[RenderGraphTexture] Cannot create texture with an invalid RHI factory.", nameof(factory));
        }

        if (descriptor.Width == 0 || descriptor.Height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "[RenderGraphTexture] Texture size must be non-zero.");
        }

        if (descriptor.ArrayLayers == 0 || descriptor.ArrayLayers > MaximumArrayLayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                $"[RenderGraphTexture] Array layer count must be between 1 and {MaximumArrayLayers}.");
        }

        if (m_Allocation is { IsValid: true } &&
            m_Descriptor.Equals(descriptor))
        {
            return;
        }

        ReleaseAllocation(disposalQueue, lastSubmittedTicket);
        m_Descriptor = descriptor;
        m_Allocation = RenderGraphTextureAllocation.Create(factory, descriptor);
        CurrentState = RenderResourceState.Unknown;

        Logger.Log(
            $"[RenderGraphTexture] Created transient texture | Name: {descriptor.DebugName} | Size: {descriptor.Width}x{descriptor.Height}x{descriptor.ArrayLayers} | Format: {descriptor.Format} | Usage: 0x{descriptor.Usage:X} | Aspect: {descriptor.AspectMask} | Sampled: {descriptor.RegisterBindlessSampled} | Image: {Image.Index}:{Image.Generation} | View: {ImageView.Index}:{ImageView.Generation} | BindlessImage: {BindlessImageIndex} | BindlessSampler: {BindlessSamplerIndex}");
    }

    internal void DisposeDeferred(
        DeferredRenderResourceDisposalQueue? disposalQueue,
        ulong lastSubmittedTicket)
    {
        if (m_Disposed)
        {
            return;
        }

        ReleaseAllocation(disposalQueue, lastSubmittedTicket);
        m_Disposed = true;
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleaseAllocation(disposalQueue: null, lastSubmittedTicket: 0);
        m_Disposed = true;
    }

    private void ReleaseAllocation(
        DeferredRenderResourceDisposalQueue? disposalQueue,
        ulong lastSubmittedTicket)
    {
        var allocation = m_Allocation;
        if (allocation == null)
        {
            return;
        }

        m_Allocation = null;
        CurrentState = RenderResourceState.Unknown;
        if (disposalQueue != null)
        {
            disposalQueue.Enqueue(allocation, lastSubmittedTicket);
        }
        else
        {
            allocation.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(RenderGraphTexture));
        }
    }

    private void ValidateArrayLayer(uint arrayLayer)
    {
        if (arrayLayer >= m_Descriptor.ArrayLayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arrayLayer),
                $"[RenderGraphTexture] Array layer {arrayLayer} is outside [0, {m_Descriptor.ArrayLayers}).");
        }
    }

    private sealed class RenderGraphTextureAllocation : IDisposable
    {
        private readonly RHIFactory m_Factory;
        private bool m_Disposed;

        private RenderGraphTextureAllocation(
            RHIFactory factory,
            RHIImageHandle image,
            RHIImageViewHandle imageView,
            RHIImageViewHandle[] layerImageViews,
            RHISamplerHandle sampler,
            uint[] bindlessImageIndices,
            uint bindlessSamplerIndex)
        {
            m_Factory = factory;
            Image = image;
            ImageView = imageView;
            LayerImageViews = layerImageViews;
            Sampler = sampler;
            BindlessImageIndices = bindlessImageIndices;
            BindlessSamplerIndex = bindlessSamplerIndex;
        }

        public RHIImageHandle Image { get; private set; }
        public RHIImageViewHandle ImageView { get; private set; }
        public RHIImageViewHandle[] LayerImageViews { get; private set; }
        public RHISamplerHandle Sampler { get; private set; }
        public uint[] BindlessImageIndices { get; private set; }
        public uint BindlessImageIndex => BindlessImageIndices.Length > 0
            ? BindlessImageIndices[0]
            : InvalidBindlessIndex;
        public uint BindlessSamplerIndex { get; private set; }
        public bool IsValid =>
            Image.IsValid &&
            ImageView.IsValid &&
            LayerImageViews.All(static view => view.IsValid) &&
            (!Sampler.IsValid ||
             (BindlessImageIndices.Length > 0 &&
              BindlessImageIndices.All(static index => index != InvalidBindlessIndex) &&
              BindlessSamplerIndex != InvalidBindlessIndex));

        public RHIImageViewHandle GetLayerImageView(uint arrayLayer)
        {
            return LayerImageViews.Length == 0
                ? ImageView
                : LayerImageViews[checked((int)arrayLayer)];
        }

        public uint GetLayerBindlessImageIndex(uint arrayLayer)
        {
            return BindlessImageIndices.Length == 0
                ? InvalidBindlessIndex
                : BindlessImageIndices[checked((int)arrayLayer)];
        }

        public static RenderGraphTextureAllocation Create(
            RHIFactory factory,
            RenderGraphTextureDescriptor descriptor)
        {
            var image = RHIImageHandle.Invalid;
            var imageView = RHIImageViewHandle.Invalid;
            RHIImageViewHandle[] layerImageViews = Array.Empty<RHIImageViewHandle>();
            var sampler = RHISamplerHandle.Invalid;
            uint[] bindlessImageIndices = Array.Empty<uint>();
            var bindlessSamplerIndex = InvalidBindlessIndex;

            try
            {
                image = factory.CreateImage(
                    descriptor.Width,
                    descriptor.Height,
                    1,
                    1,
                    descriptor.ArrayLayers,
                    descriptor.Format,
                    descriptor.Usage,
                    ERHIMemoryUsage.GpuOnly,
                    descriptor.DebugName);
                if (!image.IsValid)
                {
                    throw new InvalidOperationException($"[RenderGraphTexture] Failed to create image '{descriptor.DebugName}'.");
                }

                imageView = factory.CreateImageView(
                    image,
                    descriptor.ArrayLayers == 1
                        ? EImageViewType.IMAGE_VIEW_TYPE_2D
                        : EImageViewType.IMAGE_VIEW_TYPE_2D_ARRAY,
                    descriptor.Format,
                    (uint)descriptor.AspectMask,
                    0,
                    1,
                    0,
                    descriptor.ArrayLayers);
                if (!imageView.IsValid)
                {
                    throw new InvalidOperationException($"[RenderGraphTexture] Failed to create image view '{descriptor.DebugName}'.");
                }

                if (descriptor.ArrayLayers > 1)
                {
                    layerImageViews = new RHIImageViewHandle[descriptor.ArrayLayers];
                    for (uint layer = 0; layer < descriptor.ArrayLayers; layer++)
                    {
                        RHIImageViewHandle layerView = factory.CreateImageView(
                            image,
                            EImageViewType.IMAGE_VIEW_TYPE_2D,
                            descriptor.Format,
                            (uint)descriptor.AspectMask,
                            0,
                            1,
                            layer,
                            1);
                        if (!layerView.IsValid)
                        {
                            throw new InvalidOperationException(
                                $"[RenderGraphTexture] Failed to create image view '{descriptor.DebugName}' layer {layer}.");
                        }

                        layerImageViews[layer] = layerView;
                    }
                }

                if (descriptor.RegisterBindlessSampled)
                {
                    sampler = factory.CreateSampler(
                        EFilter.FILTER_LINEAR,
                        EFilter.FILTER_LINEAR,
                        ESamplerMipmapMode.SAMPLER_MIPMAP_MODE_NEAREST,
                        ESamplerAddressMode.SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE);
                    if (!sampler.IsValid)
                    {
                        throw new InvalidOperationException($"[RenderGraphTexture] Failed to create sampler '{descriptor.DebugName}'.");
                    }

                    bindlessImageIndices = new uint[descriptor.ArrayLayers];
                    Array.Fill(bindlessImageIndices, InvalidBindlessIndex);
                    for (uint layer = 0; layer < descriptor.ArrayLayers; layer++)
                    {
                        RHIImageViewHandle sampledView = layerImageViews.Length == 0
                            ? imageView
                            : layerImageViews[layer];
                        uint bindlessIndex = factory.RegisterBindlessResourceImage(sampledView);
                        if (bindlessIndex == InvalidBindlessIndex)
                        {
                            throw new InvalidOperationException(
                                $"[RenderGraphTexture] Failed to register bindless image '{descriptor.DebugName}' layer {layer}.");
                        }

                        bindlessImageIndices[layer] = bindlessIndex;
                    }

                    bindlessSamplerIndex = factory.RegisterBindlessResourceSampler(sampler);
                    if (bindlessSamplerIndex == InvalidBindlessIndex)
                    {
                        throw new InvalidOperationException($"[RenderGraphTexture] Failed to register bindless descriptors '{descriptor.DebugName}'.");
                    }
                }

                return new RenderGraphTextureAllocation(
                    factory,
                    image,
                    imageView,
                    layerImageViews,
                    sampler,
                    bindlessImageIndices,
                    bindlessSamplerIndex);
            }
            catch
            {
                ReleaseCreatedResources(
                    factory,
                    image,
                    imageView,
                    layerImageViews,
                    sampler,
                    bindlessImageIndices,
                    bindlessSamplerIndex);
                throw;
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }

            ReleaseCreatedResources(
                m_Factory,
                Image,
                ImageView,
                LayerImageViews,
                Sampler,
                BindlessImageIndices,
                BindlessSamplerIndex);
            Image = RHIImageHandle.Invalid;
            ImageView = RHIImageViewHandle.Invalid;
            LayerImageViews = Array.Empty<RHIImageViewHandle>();
            Sampler = RHISamplerHandle.Invalid;
            BindlessImageIndices = Array.Empty<uint>();
            BindlessSamplerIndex = InvalidBindlessIndex;
            m_Disposed = true;
        }

        private static void ReleaseCreatedResources(
            RHIFactory factory,
            RHIImageHandle image,
            RHIImageViewHandle imageView,
            RHIImageViewHandle[] layerImageViews,
            RHISamplerHandle sampler,
            uint[] bindlessImageIndices,
            uint bindlessSamplerIndex)
        {
            if (!factory.IsValid)
            {
                return;
            }

            if (bindlessSamplerIndex != InvalidBindlessIndex)
            {
                factory.UnregisterBindlessResourceSampler(bindlessSamplerIndex);
            }

            for (int index = bindlessImageIndices.Length - 1; index >= 0; index--)
            {
                if (bindlessImageIndices[index] != InvalidBindlessIndex)
                {
                    factory.UnregisterBindlessResourceImage(bindlessImageIndices[index]);
                }
            }

            if (sampler.IsValid)
            {
                factory.ReleaseSampler(sampler);
            }

            for (int index = layerImageViews.Length - 1; index >= 0; index--)
            {
                if (layerImageViews[index].IsValid)
                {
                    factory.ReleaseImageView(layerImageViews[index]);
                }
            }

            if (imageView.IsValid)
            {
                factory.ReleaseImageView(imageView);
            }

            if (image.IsValid)
            {
                factory.ReleaseImage(image);
            }
        }
    }
}
