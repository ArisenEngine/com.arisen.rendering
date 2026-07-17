using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

public sealed class RHIEnvironmentLightingResource : IDisposable
{
    private static readonly MaterialTextureSamplerSettings s_LatLongIrradianceSampler = new(
        MaterialTextureFilter.Linear,
        MaterialTextureFilter.Linear,
        MaterialTextureMipmapMode.Nearest,
        MaterialTextureWrapMode.Repeat,
        MaterialTextureWrapMode.ClampToEdge);

    private static readonly MaterialTextureSamplerSettings s_LatLongSpecularSampler = new(
        MaterialTextureFilter.Linear,
        MaterialTextureFilter.Linear,
        MaterialTextureMipmapMode.Linear,
        MaterialTextureWrapMode.Repeat,
        MaterialTextureWrapMode.ClampToEdge);

    private static readonly MaterialTextureSamplerSettings s_BrdfSampler = new(
        MaterialTextureFilter.Linear,
        MaterialTextureFilter.Linear,
        MaterialTextureMipmapMode.Nearest,
        MaterialTextureWrapMode.ClampToEdge,
        MaterialTextureWrapMode.ClampToEdge);

    private readonly IAssetDatabase m_AssetDatabase;
    private CookedEnvironmentLighting m_CookedLighting;
    private RHITexture2DAllocation? m_Irradiance;
    private RHITexture2DAllocation? m_PrefilteredSpecular;
    private RHITexture2DAllocation? m_BrdfIntegrationLut;
    private bool m_Disposed;

    public EnvironmentTextureAsset Asset { get; }
    public AssetDependencyStamp DependencyStamp { get; }
    public uint IrradianceImageIndex =>
        m_Irradiance?.BindlessImageIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public uint IrradianceSamplerIndex =>
        m_Irradiance?.BindlessSamplerIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public uint PrefilteredSpecularImageIndex =>
        m_PrefilteredSpecular?.BindlessImageIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public uint PrefilteredSpecularSamplerIndex =>
        m_PrefilteredSpecular?.BindlessSamplerIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public uint BrdfIntegrationLutImageIndex =>
        m_BrdfIntegrationLut?.BindlessImageIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public uint BrdfIntegrationLutSamplerIndex =>
        m_BrdfIntegrationLut?.BindlessSamplerIndex ?? RHITexture2DAllocation.InvalidBindlessIndex;
    public float PrefilteredSpecularMaxLod => Math.Max(0, m_CookedLighting.SpecularMipCount - 1);
    public float RotationRadians => Asset.RotationDegrees * (MathF.PI / 180.0f);
    public float Intensity => Asset.Intensity;
    public bool IsValid =>
        m_Irradiance is { IsValid: true } &&
        m_PrefilteredSpecular is { IsValid: true } &&
        m_BrdfIntegrationLut is { IsValid: true };

    public RHIEnvironmentLightingResource(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException(
                "[RHIEnvironmentLightingResource] Cannot create IBL resources with an invalid RHI device.",
                nameof(device));
        }

        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        DependencyStamp = AssetDependencyTracker.GetEnvironmentTextureStamp(m_AssetDatabase, Asset);

        try
        {
            m_CookedLighting = EnvironmentLightingAssetCooker.LoadOrCook(m_AssetDatabase, Asset);
            var bytes = m_AssetDatabase.GetCookedAssetBytes(m_CookedLighting.Handle);
            m_Irradiance = CreateAllocation(
                device,
                bytes.Slice(
                    m_CookedLighting.IrradianceDataOffset,
                    m_CookedLighting.IrradianceDataSize),
                m_CookedLighting.IrradianceWidth,
                m_CookedLighting.IrradianceHeight,
                m_CookedLighting.IrradianceMipCount,
                s_LatLongIrradianceSampler,
                $"{Asset.Name}.DiffuseIrradiance");
            m_PrefilteredSpecular = CreateAllocation(
                device,
                bytes.Slice(
                    m_CookedLighting.SpecularDataOffset,
                    m_CookedLighting.SpecularDataSize),
                m_CookedLighting.SpecularWidth,
                m_CookedLighting.SpecularHeight,
                m_CookedLighting.SpecularMipCount,
                s_LatLongSpecularSampler,
                $"{Asset.Name}.PrefilteredSpecular");
            m_BrdfIntegrationLut = CreateAllocation(
                device,
                bytes.Slice(
                    m_CookedLighting.BrdfDataOffset,
                    m_CookedLighting.BrdfDataSize),
                m_CookedLighting.BrdfWidth,
                m_CookedLighting.BrdfHeight,
                m_CookedLighting.BrdfMipCount,
                s_BrdfSampler,
                $"{Asset.Name}.BrdfIntegrationLut");

            Logger.Log(
                $"[RHIEnvironmentLightingResource] Uploaded IBL resources | Name: {Asset.Name} | Irradiance: {m_CookedLighting.IrradianceWidth}x{m_CookedLighting.IrradianceHeight} image={IrradianceImageIndex} sampler={IrradianceSamplerIndex} | Specular: {m_CookedLighting.SpecularWidth}x{m_CookedLighting.SpecularHeight} mips={m_CookedLighting.SpecularMipCount} image={PrefilteredSpecularImageIndex} sampler={PrefilteredSpecularSamplerIndex} | BRDF: {m_CookedLighting.BrdfWidth}x{m_CookedLighting.BrdfHeight} image={BrdfIntegrationLutImageIndex} sampler={BrdfIntegrationLutSamplerIndex}");
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

        m_BrdfIntegrationLut?.Dispose();
        m_BrdfIntegrationLut = null;
        m_PrefilteredSpecular?.Dispose();
        m_PrefilteredSpecular = null;
        m_Irradiance?.Dispose();
        m_Irradiance = null;

        if (m_CookedLighting.IsValid)
        {
            m_AssetDatabase.Release(m_CookedLighting.Handle);
        }

        m_CookedLighting = default;
        m_Disposed = true;
    }

    private static RHITexture2DAllocation CreateAllocation(
        RHIDevice device,
        ReadOnlyMemory<byte> pixels,
        uint width,
        uint height,
        int mipCount,
        MaterialTextureSamplerSettings sampler,
        string name)
    {
        return new RHITexture2DAllocation(
            device,
            pixels,
            width,
            height,
            checked((uint)mipCount),
            EFormat.FORMAT_R16G16B16A16_SFLOAT,
            sampler,
            name);
    }
}
