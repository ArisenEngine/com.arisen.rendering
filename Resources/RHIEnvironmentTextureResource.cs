using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

public sealed class RHIEnvironmentTextureResource : IDisposable
{
    private static readonly MaterialTextureSamplerSettings s_LatLongSampler = new(
        MaterialTextureFilter.Linear,
        MaterialTextureFilter.Linear,
        MaterialTextureMipmapMode.Nearest,
        MaterialTextureWrapMode.Repeat,
        MaterialTextureWrapMode.ClampToEdge);

    private readonly IAssetDatabase m_AssetDatabase;
    private CookedEnvironmentTexture m_CookedTexture;
    private RHITexture2DAllocation? m_Allocation;
    private bool m_Disposed;

    public EnvironmentTextureAsset Asset { get; }
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
    public float RotationRadians => m_CookedTexture.RotationDegrees * (MathF.PI / 180.0f);
    public float Intensity => m_CookedTexture.Intensity;
    public AssetDependencyStamp DependencyStamp { get; }
    public bool IsValid => m_Allocation is { IsValid: true };

    public RHIEnvironmentTextureResource(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException(
                "[RHIEnvironmentTextureResource] Cannot create an environment texture with an invalid RHI device.",
                nameof(device));
        }

        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        DependencyStamp = AssetDependencyTracker.GetEnvironmentTextureStamp(m_AssetDatabase, Asset);

        try
        {
            m_CookedTexture = EnvironmentTextureAssetCooker.LoadOrCook(m_AssetDatabase, Asset);
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
                s_LatLongSampler,
                Asset.Name);

            Logger.Log(
                $"[RHIEnvironmentTextureResource] Uploaded lat-long environment | Name: {Asset.Name} | Size: {Width}x{Height} | Format: {Format} | Rotation: {m_CookedTexture.RotationDegrees:0.###} deg | Intensity: {Intensity:0.###} | BindlessImage: {BindlessImageIndex} | BindlessSampler: {BindlessSamplerIndex}");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsSourceStale()
    {
        return AssetDependencyTracker.GetEnvironmentTextureStamp(m_AssetDatabase, Asset) !=
               DependencyStamp;
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

    private static EFormat ResolveRhiFormat(CookedEnvironmentTexture texture)
    {
        return texture.Format switch
        {
            EnvironmentTextureCookedFormat.R16G16B16A16SFloat =>
                EFormat.FORMAT_R16G16B16A16_SFLOAT,
            _ => throw new NotSupportedException(
                $"Environment texture format '{texture.Format}' is not supported by the RHI uploader.")
        };
    }
}
