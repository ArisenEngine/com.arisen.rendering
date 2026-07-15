using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

public readonly record struct RenderGraphTextureDescriptor(
    string DebugName,
    uint Width,
    uint Height,
    EFormat Format,
    uint Usage,
    EImageAspectFlagBits AspectMask,
    bool RegisterBindlessSampled)
{
    public static RenderGraphTextureDescriptor ColorAttachmentSampled2D(
        string debugName,
        uint width,
        uint height,
        EFormat format)
    {
        return new RenderGraphTextureDescriptor(
            debugName,
            width,
            height,
            format,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
            (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
            EImageAspectFlagBits.IMAGE_ASPECT_COLOR_BIT,
            RegisterBindlessSampled: true);
    }
}

public sealed class RenderGraphTexture : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

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
    public EFormat Format => m_Descriptor.Format;
    public EImageAspectFlagBits AspectMask => m_Descriptor.AspectMask;
    public bool IsValid => m_Allocation is { IsValid: true };

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
            $"[RenderGraphTexture] Created transient texture | Name: {descriptor.DebugName} | Size: {descriptor.Width}x{descriptor.Height} | Format: {descriptor.Format} | Image: {Image.Index}:{Image.Generation} | View: {ImageView.Index}:{ImageView.Generation} | BindlessImage: {BindlessImageIndex} | BindlessSampler: {BindlessSamplerIndex}");
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

    private sealed class RenderGraphTextureAllocation : IDisposable
    {
        private readonly RHIFactory m_Factory;
        private bool m_Disposed;

        private RenderGraphTextureAllocation(
            RHIFactory factory,
            RHIImageHandle image,
            RHIImageViewHandle imageView,
            RHISamplerHandle sampler,
            uint bindlessImageIndex,
            uint bindlessSamplerIndex)
        {
            m_Factory = factory;
            Image = image;
            ImageView = imageView;
            Sampler = sampler;
            BindlessImageIndex = bindlessImageIndex;
            BindlessSamplerIndex = bindlessSamplerIndex;
        }

        public RHIImageHandle Image { get; private set; }
        public RHIImageViewHandle ImageView { get; private set; }
        public RHISamplerHandle Sampler { get; private set; }
        public uint BindlessImageIndex { get; private set; }
        public uint BindlessSamplerIndex { get; private set; }
        public bool IsValid =>
            Image.IsValid &&
            ImageView.IsValid &&
            (!Sampler.IsValid ||
             (BindlessImageIndex != InvalidBindlessIndex &&
              BindlessSamplerIndex != InvalidBindlessIndex));

        public static RenderGraphTextureAllocation Create(
            RHIFactory factory,
            RenderGraphTextureDescriptor descriptor)
        {
            var image = RHIImageHandle.Invalid;
            var imageView = RHIImageViewHandle.Invalid;
            var sampler = RHISamplerHandle.Invalid;
            var bindlessImageIndex = InvalidBindlessIndex;
            var bindlessSamplerIndex = InvalidBindlessIndex;

            try
            {
                image = factory.CreateImage(
                    descriptor.Width,
                    descriptor.Height,
                    1,
                    1,
                    1,
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
                    EImageViewType.IMAGE_VIEW_TYPE_2D,
                    descriptor.Format,
                    (uint)descriptor.AspectMask,
                    0,
                    1,
                    0,
                    1);
                if (!imageView.IsValid)
                {
                    throw new InvalidOperationException($"[RenderGraphTexture] Failed to create image view '{descriptor.DebugName}'.");
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

                    bindlessImageIndex = factory.RegisterBindlessResourceImage(imageView);
                    bindlessSamplerIndex = factory.RegisterBindlessResourceSampler(sampler);
                    if (bindlessImageIndex == InvalidBindlessIndex ||
                        bindlessSamplerIndex == InvalidBindlessIndex)
                    {
                        throw new InvalidOperationException($"[RenderGraphTexture] Failed to register bindless descriptors '{descriptor.DebugName}'.");
                    }
                }

                return new RenderGraphTextureAllocation(
                    factory,
                    image,
                    imageView,
                    sampler,
                    bindlessImageIndex,
                    bindlessSamplerIndex);
            }
            catch
            {
                ReleaseCreatedResources(
                    factory,
                    image,
                    imageView,
                    sampler,
                    bindlessImageIndex,
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
                Sampler,
                BindlessImageIndex,
                BindlessSamplerIndex);
            Image = RHIImageHandle.Invalid;
            ImageView = RHIImageViewHandle.Invalid;
            Sampler = RHISamplerHandle.Invalid;
            BindlessImageIndex = InvalidBindlessIndex;
            BindlessSamplerIndex = InvalidBindlessIndex;
            m_Disposed = true;
        }

        private static void ReleaseCreatedResources(
            RHIFactory factory,
            RHIImageHandle image,
            RHIImageViewHandle imageView,
            RHISamplerHandle sampler,
            uint bindlessImageIndex,
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

            if (bindlessImageIndex != InvalidBindlessIndex)
            {
                factory.UnregisterBindlessResourceImage(bindlessImageIndex);
            }

            if (sampler.IsValid)
            {
                factory.ReleaseSampler(sampler);
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
