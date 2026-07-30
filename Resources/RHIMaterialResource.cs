using System.Runtime.InteropServices;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering.Resources;

public readonly record struct MaterialTexture2DBinding(
    string Name,
    uint Slot,
    uint ImageIndex,
    uint SamplerIndex,
    MaterialTextureTransform Transform);

public interface IRHITexture2DLease : IDisposable
{
    bool IsValid { get; }
    uint BindlessImageIndex { get; }
    uint BindlessSamplerIndex { get; }
}

public interface IRHITexture2DResourceCache
{
    IRHITexture2DLease Acquire(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        Texture2DAsset asset,
        MaterialTextureSamplerSettings samplerSettings);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct MaterialTexture2DBindlessConstants
{
    public readonly uint ImageIndex;
    public readonly uint SamplerIndex;

    public MaterialTexture2DBindlessConstants(uint imageIndex, uint samplerIndex)
    {
        ImageIndex = imageIndex;
        SamplerIndex = samplerIndex;
    }
}

public sealed class RHIMaterialResource : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    private readonly MaterialAsset m_Asset;
    private readonly IRHITexture2DLease[] m_Textures;
    private readonly MaterialTexture2DBinding[] m_TextureBindings;
    private readonly MaterialScalarProperty[] m_ScalarProperties;
    private readonly MaterialVector4Property[] m_Vector4Properties;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly CookedAssetHandle m_CookedMaterialHandle;
    private bool m_Disposed;

    public MaterialAsset Asset => m_Asset;
    public ShaderAsset Shader => m_Asset.Shader;
    public string ShaderVariantIdentity { get; }
    public MaterialRenderState RenderState => m_Asset.RenderState;
    public AssetDependencyStamp DependencyStamp { get; }
    public AssetDependencyStamp ShaderDependencyStamp { get; }
    public int Texture2DCount => m_TextureBindings.Length;
    public bool IsValid { get; private set; }

    public RHIMaterialResource(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        MaterialAsset asset,
        CookedAssetHandle cookedMaterialHandle = default,
        IRHITexture2DResourceCache? textureCache = null)
    {
        if (!device.IsValid)
        {
            throw new ArgumentException("[RHIMaterialResource] Cannot create a material with an invalid RHI device.", nameof(device));
        }

        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_CookedMaterialHandle = cookedMaterialHandle;

        m_Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        if (m_Asset.Shader == null)
        {
            throw new ArgumentException("[RHIMaterialResource] Material shader cannot be null.", nameof(asset));
        }

        DependencyStamp = AssetDependencyTracker.GetMaterialStamp(m_AssetDatabase, m_Asset);
        ShaderDependencyStamp = AssetDependencyTracker.GetShaderStamp(m_AssetDatabase, m_Asset.Shader);
        ShaderVariantIdentity = m_Asset.Shader.GetVariantIdentity();

        var textureRefs = m_Asset.Texture2DRefs ?? Array.Empty<MaterialTexture2DRef>();
        m_Textures = new IRHITexture2DLease[textureRefs.Count];
        m_TextureBindings = new MaterialTexture2DBinding[textureRefs.Count];
        m_ScalarProperties = (m_Asset.ScalarProperties ?? Array.Empty<MaterialScalarProperty>()).ToArray();
        m_Vector4Properties = (m_Asset.Vector4Properties ?? Array.Empty<MaterialVector4Property>()).ToArray();

        try
        {
            for (int i = 0; i < textureRefs.Count; i++)
            {
                var textureRef = textureRefs[i];
                if (string.IsNullOrWhiteSpace(textureRef.Name))
                {
                    throw new InvalidOperationException($"[RHIMaterialResource] Material '{m_Asset.Name}' has an unnamed Texture2D binding at index {i}.");
                }

                IRHITexture2DLease texture = textureCache?.Acquire(
                        device,
                        m_AssetDatabase,
                        textureRef.Texture,
                        textureRef.ResolvedSampler)
                    ?? new OwnedTextureLease(new RHITexture2DResource(
                        device,
                        m_AssetDatabase,
                        textureRef.Texture,
                        textureRef.ResolvedSampler));
                if (!texture.IsValid ||
                    texture.BindlessImageIndex == InvalidBindlessIndex ||
                    texture.BindlessSamplerIndex == InvalidBindlessIndex)
                {
                    texture.Dispose();
                    throw new InvalidOperationException(
                        $"[RHIMaterialResource] Material '{m_Asset.Name}' Texture2D '{textureRef.Name}' is not ready for bindless sampling.");
                }

                m_Textures[i] = texture;
                m_TextureBindings[i] = new MaterialTexture2DBinding(
                    textureRef.Name,
                    textureRef.Slot,
                    texture.BindlessImageIndex,
                    texture.BindlessSamplerIndex,
                    textureRef.ResolvedTransform);
            }

            IsValid = true;
            Logger.Log(
                $"[RHIMaterialResource] Prepared material | Name: {m_Asset.Name} | Shader: {m_Asset.Shader.Name} | Texture2DCount: {Texture2DCount} | ScalarProperties: {m_ScalarProperties.Length} | Vector4Properties: {m_Vector4Properties.Length}");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool TryGetTexture2DConstants(string name, out MaterialTexture2DBindlessConstants constants)
    {
        for (int i = 0; i < m_TextureBindings.Length; i++)
        {
            var binding = m_TextureBindings[i];
            if (string.Equals(binding.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                constants = new MaterialTexture2DBindlessConstants(binding.ImageIndex, binding.SamplerIndex);
                return true;
            }
        }

        constants = default;
        return false;
    }

    public MaterialTexture2DBindlessConstants GetTexture2DConstants(string name)
    {
        if (TryGetTexture2DConstants(name, out var constants))
        {
            return constants;
        }

        throw new InvalidOperationException(
            $"[RHIMaterialResource] Material '{m_Asset.Name}' does not define Texture2D binding '{name}'.");
    }

    public bool TryGetTexture2DTransform(string name, out MaterialTextureTransform transform)
    {
        for (int i = 0; i < m_TextureBindings.Length; i++)
        {
            var binding = m_TextureBindings[i];
            if (string.Equals(binding.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                transform = binding.Transform;
                return true;
            }
        }

        transform = MaterialTextureTransform.Identity;
        return false;
    }

    public bool TryGetScalarProperty(string name, out float value)
    {
        for (int i = 0; i < m_ScalarProperties.Length; i++)
        {
            var property = m_ScalarProperties[i];
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public float GetScalarPropertyOrDefault(string name, float defaultValue)
    {
        return TryGetScalarProperty(name, out var value) ? value : defaultValue;
    }

    public bool TryGetVector4Property(string name, out Vector4 value)
    {
        for (int i = 0; i < m_Vector4Properties.Length; i++)
        {
            var property = m_Vector4Properties[i];
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public Vector4 GetVector4PropertyOrDefault(string name, Vector4 defaultValue)
    {
        return TryGetVector4Property(name, out var value) ? value : defaultValue;
    }

    public bool IsSourceStale()
    {
        return AssetDependencyTracker.GetMaterialStamp(m_AssetDatabase, m_Asset) != DependencyStamp;
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        for (int i = m_Textures.Length - 1; i >= 0; i--)
        {
            m_Textures[i]?.Dispose();
        }

        if (m_CookedMaterialHandle.IsValid)
        {
            m_AssetDatabase.Release(m_CookedMaterialHandle);
        }

        IsValid = false;
        m_Disposed = true;
    }

    private sealed class OwnedTextureLease : IRHITexture2DLease
    {
        private RHITexture2DResource? m_Resource;

        public OwnedTextureLease(RHITexture2DResource resource)
        {
            m_Resource = resource;
        }

        public bool IsValid => m_Resource is { IsValid: true };
        public uint BindlessImageIndex =>
            m_Resource?.BindlessImageIndex ?? InvalidBindlessIndex;
        public uint BindlessSamplerIndex =>
            m_Resource?.BindlessSamplerIndex ?? InvalidBindlessIndex;

        public void Dispose()
        {
            Interlocked.Exchange(ref m_Resource, null)?.Dispose();
        }
    }
}
