using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

public sealed class RHITexture2DResource : IDisposable
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Texture2DAsset m_Asset;
    private readonly MaterialTextureSamplerSettings m_SamplerSettings;
    private CookedTexture2D m_CookedTexture;
    private RHITexture2DAllocation? m_Allocation;
    private bool m_Disposed;

    public uint Width => m_CookedTexture.Width;
    public uint Height => m_CookedTexture.Height;
    public EFormat Format => m_Allocation?.Format ?? EFormat.FORMAT_UNDEFINED;
    public RHIImageHandle Image => m_Allocation?.Image ?? RHIImageHandle.Invalid;
    public RHIImageViewHandle ImageView => m_Allocation?.ImageView ?? RHIImageViewHandle.Invalid;
    public RHISamplerHandle Sampler => m_Allocation?.Sampler ?? RHISamplerHandle.Invalid;
    public uint BindlessImageIndex =>
        m_Allocation?.BindlessImageIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public uint BindlessSamplerIndex =>
        m_Allocation?.BindlessSamplerIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public AssetDependencyStamp DependencyStamp { get; private set; }
    public bool IsValid => m_Allocation is { IsValid: true };

    public RHITexture2DResource(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        Texture2DAsset asset,
        MaterialTextureSamplerSettings? samplerSettings = null)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException(
                "[RHITexture2DResource] Cannot create a texture with an invalid RHI device.",
                nameof(device));
        }

        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        m_SamplerSettings = samplerSettings ?? MaterialTextureSamplerSettings.Default;

        try
        {
            CreateFromCookedAsset(device);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateFromCookedAsset(RHIDevice device)
    {
        DependencyStamp = AssetDependencyTracker.GetAssetStamp(m_AssetDatabase, m_Asset.Guid);
        m_CookedTexture = Texture2DAssetCooker.LoadOrCook(m_AssetDatabase, m_Asset);
        var cookedBytes = m_AssetDatabase.GetCookedAssetBytes(m_CookedTexture.Handle);
        var pixelBytes = cookedBytes.Slice(
            m_CookedTexture.PixelDataOffset,
            m_CookedTexture.PixelDataSize);

        m_Allocation = new RHITexture2DAllocation(
            device,
            pixelBytes,
            m_CookedTexture.Width,
            m_CookedTexture.Height,
            checked((uint)m_CookedTexture.MipCount),
            ResolveRhiFormat(m_CookedTexture),
            m_SamplerSettings,
            m_Asset.Name);

        Logger.Log(
            $"[RHITexture2DResource] Uploaded texture | Name: {m_Asset.Name} | Size: {Width}x{Height} | Format: {Format} | Image: {Image.Index}:{Image.Generation} | View: {ImageView.Index}:{ImageView.Generation} | BindlessImage: {BindlessImageIndex} | BindlessSampler: {BindlessSamplerIndex}");
    }

    private static EFormat ResolveRhiFormat(CookedTexture2D texture)
    {
        if (texture.Format != Texture2DCookedFormat.R8G8B8A8UNorm)
        {
            throw new NotSupportedException(
                $"Texture format '{texture.Format}' is not supported by the RHI texture uploader yet.");
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

        m_Allocation?.Dispose();
        m_Allocation = null;

        if (m_CookedTexture.IsValid)
        {
            m_AssetDatabase.Release(m_CookedTexture.Handle);
        }

        m_CookedTexture = default;
        m_Disposed = true;
    }
}
