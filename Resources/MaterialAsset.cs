using System.Buffers.Binary;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;
using Arisen.Native.RHI;

namespace ArisenEngine.Rendering.Resources;

public static class MaterialTextureSlots
{
    public const string BaseColor = "BaseColor";
}

public static class MaterialPropertySlots
{
    public const string BaseColorFactor = "BaseColorFactor";
    public const string MetallicFactor = "MetallicFactor";
    public const string RoughnessFactor = "RoughnessFactor";
}

public readonly record struct MaterialTexture2DRef(
    string Name,
    Texture2DAsset Texture,
    uint Slot = 0);

public readonly record struct MaterialScalarProperty(
    string Name,
    float Value);

public readonly record struct MaterialVector4Property(
    string Name,
    Vector4 Value);

public readonly record struct MaterialShaderContract(
    IReadOnlyList<string> RequiredTexture2DRefs,
    IReadOnlyList<string> RequiredScalarProperties,
    IReadOnlyList<string> RequiredVector4Properties)
{
    public static MaterialShaderContract Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());
}

public readonly record struct MaterialRenderState(
    ECullModeFlagBits CullMode,
    EFrontFace FrontFace,
    bool BlendEnabled,
    EBlendFactor SrcColorBlendFactor,
    EBlendFactor DstColorBlendFactor,
    EBlendOp ColorBlendOp)
{
    public static MaterialRenderState Default { get; } = new(
        ECullModeFlagBits.CULL_MODE_NONE,
        EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE,
        false,
        EBlendFactor.BLEND_FACTOR_ONE,
        EBlendFactor.BLEND_FACTOR_ZERO,
        EBlendOp.BLEND_OP_ADD);
}

public sealed record MaterialAsset(
    Guid Guid,
    string Name,
    ShaderAsset Shader,
    IReadOnlyList<MaterialTexture2DRef> Texture2DRefs,
    IReadOnlyList<MaterialScalarProperty> ScalarProperties,
    IReadOnlyList<MaterialVector4Property> Vector4Properties,
    MaterialRenderState RenderState);

public readonly record struct CookedMaterial(
    MaterialAsset Asset,
    string Variant,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class MaterialAssetLoader
{
    private const string MaterialAssetType = "Material";

    public static MaterialAsset Load(IAssetDatabase assetDatabase, Guid materialGuid)
    {
        return LoadSource(assetDatabase, materialGuid);
    }

    public static MaterialAsset LoadSource(IAssetDatabase assetDatabase, Guid materialGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetDatabase.TryGetAsset(materialGuid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[MaterialAssetLoader] Material asset '{materialGuid}' was not found.");
        }

        if (!string.Equals(sourceAsset.AssetType, MaterialAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[MaterialAssetLoader] Asset '{materialGuid}' has asset type '{sourceAsset.AssetType}', expected '{MaterialAssetType}'.");
        }

        var source = SerializationUtil.Deserialize<SerializedMaterialAsset>(sourceAsset.SourcePath, serializeIfNotExist: false);
        source.Shader.ApplyShaderSourceDefaults(assetDatabase, sourceAsset.SourcePath);
        var shaderSourceContract = source.LoadShaderSourceContract(assetDatabase);
        source.Validate(sourceAsset.SourcePath, shaderSourceContract);

        var shader = source.Shader.ToShaderAsset();
        var textureRefs = new MaterialTexture2DRef[source.Texture2DRefs.Count];
        for (int i = 0; i < source.Texture2DRefs.Count; i++)
        {
            textureRefs[i] = source.Texture2DRefs[i].ToTextureRef();
        }

        var scalarProperties = new MaterialScalarProperty[source.ScalarProperties.Count];
        for (int i = 0; i < source.ScalarProperties.Count; i++)
        {
            scalarProperties[i] = source.ScalarProperties[i].ToProperty();
        }

        var vector4Properties = new MaterialVector4Property[source.Vector4Properties.Count];
        for (int i = 0; i < source.Vector4Properties.Count; i++)
        {
            vector4Properties[i] = source.Vector4Properties[i].ToProperty();
        }

        return new MaterialAsset(
            materialGuid,
            string.IsNullOrWhiteSpace(source.Name) ? Path.GetFileNameWithoutExtension(sourceAsset.SourcePath) : source.Name,
            shader,
            textureRefs,
            scalarProperties,
            vector4Properties,
            source.ResolveRenderState(assetDatabase, sourceAsset.SourcePath));
    }

    private sealed class SerializedMaterialAsset
    {
        public string Name { get; set; } = string.Empty;
        public SerializedShaderRef Shader { get; set; } = new();
        public SerializedMaterialShaderContract ShaderContract { get; set; } = new();
        public List<SerializedMaterialTexture2DRef> Texture2DRefs { get; set; } = new();
        public List<SerializedMaterialScalarProperty> ScalarProperties { get; set; } = new();
        public List<SerializedMaterialVector4Property> Vector4Properties { get; set; } = new();
        public SerializedMaterialRenderState? RenderState { get; set; }

        public void Validate(string sourcePath, MaterialShaderContract shaderSourceContract)
        {
            if (Shader.Guid == Guid.Empty)
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' is missing a shader GUID.");
            }

            Shader.Validate(sourcePath);
            ShaderContract.Validate(sourcePath, "ShaderContract");
            RenderState?.Validate(sourcePath);

            for (int i = 0; i < Texture2DRefs.Count; i++)
            {
                Texture2DRefs[i].Validate(sourcePath, i);
            }

            for (int i = 0; i < ScalarProperties.Count; i++)
            {
                ScalarProperties[i].Validate(sourcePath, i);
            }

            for (int i = 0; i < Vector4Properties.Count; i++)
            {
                Vector4Properties[i].Validate(sourcePath, i);
            }

            ValidateUniqueNames(sourcePath, "Texture2DRefs", Texture2DRefs.Select(texture => texture.Name));
            ValidateUniqueNames(sourcePath, "ScalarProperties", ScalarProperties.Select(property => property.Name));
            ValidateUniqueNames(sourcePath, "Vector4Properties", Vector4Properties.Select(property => property.Name));
            var effectiveContract = SerializedMaterialShaderContract.Merge(shaderSourceContract, Shader.Contract, ShaderContract);
            effectiveContract.ValidateMaterialBindings(
                sourcePath,
                Texture2DRefs.Select(texture => texture.Name),
                ScalarProperties.Select(property => property.Name),
                Vector4Properties.Select(property => property.Name));
        }

        public MaterialShaderContract LoadShaderSourceContract(IAssetDatabase assetDatabase)
        {
            if (assetDatabase == null || Shader.Guid == Guid.Empty)
            {
                return MaterialShaderContract.Empty;
            }

            var shaderLab = Shader.TryLoadShaderLabSource(assetDatabase);
            if (shaderLab != null)
            {
                return shaderLab.MaterialContract;
            }

            if (!assetDatabase.TryGetAsset(Shader.Guid, out var shaderSource) ||
                !string.Equals(shaderSource.AssetType, ShaderAssetCooker.ShaderSourceAssetType, StringComparison.OrdinalIgnoreCase))
            {
                return MaterialShaderContract.Empty;
            }

            return ShaderMaterialContractAnnotations.ParseFile(shaderSource.SourcePath);
        }

        public MaterialRenderState ResolveRenderState(IAssetDatabase assetDatabase, string sourcePath)
        {
            if (RenderState != null)
            {
                return RenderState.ToRenderState(sourcePath);
            }

            return Shader.TryLoadShaderLabSource(assetDatabase)?.RenderState ?? MaterialRenderState.Default;
        }

        private static void ValidateUniqueNames(string sourcePath, string sectionName, IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!seen.Add(name))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' {sectionName} contains duplicate name '{name}'.");
                }
            }
        }
    }

    private sealed class SerializedShaderRef
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;
        public SerializedShaderVariant Variant { get; set; } = new();
        public List<SerializedShaderStage> Stages { get; set; } = new();
        public List<string> Defines { get; set; } = new();
        public List<string> Keywords { get; set; } = new();
        public List<string> Includes { get; set; } = new();
        public SerializedMaterialShaderContract Contract { get; set; } = new();
        private ShaderLabSource? m_ShaderLabSource;

        public void Validate(string sourcePath)
        {
            if (Stages.Count == 0)
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' shader must define at least one stage.");
            }

            for (int i = 0; i < Stages.Count; i++)
            {
                Stages[i].Validate(sourcePath, i);
            }

            ValidateKeywords(sourcePath);
            Contract.Validate(sourcePath, "Shader.Contract");
        }

        public ShaderAsset ToShaderAsset()
        {
            var stages = new ShaderStageAsset[Stages.Count];
            for (int i = 0; i < Stages.Count; i++)
            {
                stages[i] = Stages[i].ToStageAsset();
            }

            return new ShaderAsset(
                Guid,
                string.IsNullOrWhiteSpace(Name) ? Guid.ToString("N") : Name,
                stages,
                Variant.ToVariantKey(),
                Defines.Count == 0 ? null : Defines.ToArray(),
                Includes.Count == 0 ? null : Includes.ToArray(),
                Keywords.Count == 0 ? null : NormalizeKeywords(Keywords));
        }

        public void ApplyShaderSourceDefaults(IAssetDatabase assetDatabase, string materialSourcePath)
        {
            var shaderLab = TryLoadShaderLabSource(assetDatabase);
            if (shaderLab == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = shaderLab.Name;
            }

            if (Stages.Count == 0)
            {
                var shaderLabStages = shaderLab.BuildStages();
                for (int i = 0; i < shaderLabStages.Count; i++)
                {
                    Stages.Add(SerializedShaderStage.FromStageAsset(shaderLabStages[i]));
                }
            }

            AppendUnique(Includes, shaderLab.Includes);

            if (shaderLab.CompileTimeKeywords.Count > 0)
            {
                Logger.Log(
                    $"[MaterialAssetLoader] Material '{materialSourcePath}' shader '{Name}' declares ShaderLab compile-time keywords: {string.Join(", ", shaderLab.CompileTimeKeywords)} | Selected: [{string.Join(", ", NormalizeKeywords(Keywords))}]");
            }
        }

        public ShaderLabSource? TryLoadShaderLabSource(IAssetDatabase assetDatabase)
        {
            if (m_ShaderLabSource != null)
            {
                return m_ShaderLabSource;
            }

            if (assetDatabase == null || Guid == Guid.Empty)
            {
                return null;
            }

            if (!assetDatabase.TryGetAsset(Guid, out var shaderSource) ||
                !string.Equals(shaderSource.AssetType, ShaderAssetCooker.ShaderSourceAssetType, StringComparison.OrdinalIgnoreCase) ||
                !ShaderLabSource.IsShaderLabPath(shaderSource.SourcePath))
            {
                return null;
            }

            m_ShaderLabSource = ShaderLabSource.Load(shaderSource.SourcePath);
            return m_ShaderLabSource;
        }

        private static void AppendUnique(List<string> destination, IReadOnlyList<string> values)
        {
            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (!string.IsNullOrWhiteSpace(value) &&
                    !destination.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    destination.Add(value);
                }
            }
        }

        private void ValidateKeywords(string sourcePath)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Keywords.Count; i++)
            {
                var keyword = Keywords[i]?.Trim();
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' Shader.Keywords entry {i} is empty.");
                }

                if (string.Equals(keyword, "_", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' Shader.Keywords entry {i} selects '_', which is the disabled keyword branch and is not an active variant keyword.");
                }

                if (!IsValidKeywordIdentifier(keyword))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' Shader.Keywords entry '{keyword}' is not a valid shader keyword identifier.");
                }

                if (!selected.Add(keyword))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' Shader.Keywords contains duplicate keyword '{keyword}'.");
                }
            }

            var shaderLab = m_ShaderLabSource;
            if (shaderLab == null || Keywords.Count == 0)
            {
                return;
            }

            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < shaderLab.CompileTimeKeywords.Count; i++)
            {
                var keyword = shaderLab.CompileTimeKeywords[i];
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    !string.Equals(keyword, "_", StringComparison.Ordinal))
                {
                    declared.Add(keyword);
                }
            }

            if (declared.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[MaterialAssetLoader] Material '{sourcePath}' selects Shader.Keywords [{string.Join(", ", NormalizeKeywords(Keywords))}], but ShaderLab shader '{Name}' declares no active compile-time keywords.");
            }

            foreach (var keyword in selected)
            {
                if (!declared.Contains(keyword))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' selects Shader.Keyword '{keyword}', but ShaderLab shader '{Name}' declares only [{string.Join(", ", declared)}].");
                }
            }
        }

        private static string[] NormalizeKeywords(IReadOnlyList<string> keywords)
        {
            return ShaderVariantKey.NormalizeKeywordSet(keywords);
        }

        private static bool IsValidKeywordIdentifier(string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                return false;
            }

            if (!IsAsciiLetter(keyword[0]) && keyword[0] != '_')
            {
                return false;
            }

            for (int i = 1; i < keyword.Length; i++)
            {
                var ch = keyword[i];
                if (!IsAsciiLetter(ch) && (ch < '0' || ch > '9') && ch != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char ch)
        {
            return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
        }
    }

    private sealed class SerializedShaderVariant
    {
        public ShaderAssetBackend Backend { get; set; } = ShaderAssetBackend.Vulkan;
        public string TargetEnvironment { get; set; } = "vulkan1.3";
        public string ShaderModel { get; set; } = "6_4";
        public string OptimizationLevel { get; set; } = "0";
        public bool DebugInfo { get; set; } = true;

        public ShaderVariantKey ToVariantKey()
        {
            return new ShaderVariantKey(Backend, TargetEnvironment, ShaderModel, OptimizationLevel, DebugInfo);
        }
    }

    private sealed class SerializedShaderStage
    {
        public string Name { get; set; } = string.Empty;
        public EProgramStage ProgramStage { get; set; }
        public string EntryPoint { get; set; } = string.Empty;

        public void Validate(string sourcePath, int index)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' shader stage {index} is missing a name.");
            }

            if (string.IsNullOrWhiteSpace(EntryPoint))
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' shader stage '{Name}' is missing an entry point.");
            }
        }

        public ShaderStageAsset ToStageAsset()
        {
            return new ShaderStageAsset(Name, ProgramStage, EntryPoint);
        }

        public static SerializedShaderStage FromStageAsset(ShaderStageAsset stage)
        {
            return new SerializedShaderStage
            {
                Name = stage.Name,
                ProgramStage = stage.ProgramStage,
                EntryPoint = stage.EntryPoint
            };
        }
    }

    private sealed class SerializedMaterialShaderContract
    {
        public List<string> RequiredTexture2DRefs { get; set; } = new();
        public List<string> RequiredScalarProperties { get; set; } = new();
        public List<string> RequiredVector4Properties { get; set; } = new();

        public static SerializedMaterialShaderContract Merge(
            MaterialShaderContract shaderSourceContract,
            SerializedMaterialShaderContract? shaderContract,
            SerializedMaterialShaderContract? materialContract)
        {
            return new SerializedMaterialShaderContract
            {
                RequiredTexture2DRefs = MergeRequiredNames(
                    shaderSourceContract.RequiredTexture2DRefs,
                    shaderContract?.RequiredTexture2DRefs,
                    materialContract?.RequiredTexture2DRefs),
                RequiredScalarProperties = MergeRequiredNames(
                    shaderSourceContract.RequiredScalarProperties,
                    shaderContract?.RequiredScalarProperties,
                    materialContract?.RequiredScalarProperties),
                RequiredVector4Properties = MergeRequiredNames(
                    shaderSourceContract.RequiredVector4Properties,
                    shaderContract?.RequiredVector4Properties,
                    materialContract?.RequiredVector4Properties)
            };
        }

        public void Validate(string sourcePath, string contractPath)
        {
            ValidateRequiredNames(sourcePath, contractPath, nameof(RequiredTexture2DRefs), RequiredTexture2DRefs);
            ValidateRequiredNames(sourcePath, contractPath, nameof(RequiredScalarProperties), RequiredScalarProperties);
            ValidateRequiredNames(sourcePath, contractPath, nameof(RequiredVector4Properties), RequiredVector4Properties);
        }

        public void ValidateMaterialBindings(
            string sourcePath,
            IEnumerable<string> textureNames,
            IEnumerable<string> scalarNames,
            IEnumerable<string> vector4Names)
        {
            ValidateRequiredBindings(sourcePath, "Texture2DRefs", RequiredTexture2DRefs, textureNames);
            ValidateRequiredBindings(sourcePath, "ScalarProperties", RequiredScalarProperties, scalarNames);
            ValidateRequiredBindings(sourcePath, "Vector4Properties", RequiredVector4Properties, vector4Names);
        }

        private static List<string> MergeRequiredNames(params IReadOnlyList<string>?[] sources)
        {
            if (sources.Length == 0)
            {
                return new List<string>();
            }

            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sources.Length; i++)
            {
                AppendRequiredNames(sources[i], merged, seen);
            }

            return merged;
        }

        private static void AppendRequiredNames(
            IReadOnlyList<string>? names,
            List<string> merged,
            HashSet<string> seen)
        {
            if (names == null)
            {
                return;
            }

            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                {
                    merged.Add(name);
                }
            }
        }

        private static void ValidateRequiredNames(
            string sourcePath,
            string contractPath,
            string sectionName,
            IReadOnlyList<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' {contractPath}.{sectionName} entry {i} is empty.");
                }

                if (!seen.Add(name))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' {contractPath}.{sectionName} contains duplicate name '{name}'.");
                }
            }
        }

        private static void ValidateRequiredBindings(
            string sourcePath,
            string materialSectionName,
            IReadOnlyList<string> requiredNames,
            IEnumerable<string> providedNames)
        {
            if (requiredNames.Count == 0)
            {
                return;
            }

            var provided = new HashSet<string>(providedNames, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < requiredNames.Count; i++)
            {
                var requiredName = requiredNames[i];
                if (!provided.Contains(requiredName))
                {
                    throw new InvalidOperationException(
                        $"[MaterialAssetLoader] Material '{sourcePath}' is missing required {materialSectionName} binding '{requiredName}' declared by Shader.Contract or ShaderContract.");
                }
            }
        }
    }

    private sealed class SerializedMaterialTexture2DRef
    {
        public string Name { get; set; } = string.Empty;
        public uint Slot { get; set; }
        public SerializedTexture2DRef Texture { get; set; } = new();

        public void Validate(string sourcePath, int index)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' Texture2D ref {index} is missing a binding name.");
            }

            if (Texture.Guid == Guid.Empty)
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' Texture2D ref '{Name}' is missing a texture GUID.");
            }
        }

        public MaterialTexture2DRef ToTextureRef()
        {
            return new MaterialTexture2DRef(Name, Texture.ToTextureAsset(), Slot);
        }
    }

    private sealed class SerializedMaterialScalarProperty
    {
        public string Name { get; set; } = string.Empty;
        public float Value { get; set; }

        public void Validate(string sourcePath, int index)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' scalar property {index} is missing a name.");
            }
        }

        public MaterialScalarProperty ToProperty()
        {
            return new MaterialScalarProperty(Name, Value);
        }
    }

    private sealed class SerializedMaterialVector4Property
    {
        public string Name { get; set; } = string.Empty;
        public SerializedVector4 Value { get; set; } = SerializedVector4.One;

        public void Validate(string sourcePath, int index)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException($"[MaterialAssetLoader] Material '{sourcePath}' Vector4 property {index} is missing a name.");
            }
        }

        public MaterialVector4Property ToProperty()
        {
            return new MaterialVector4Property(Name, Value.ToVector4());
        }
    }

    private sealed class SerializedVector4
    {
        public static SerializedVector4 One => new() { X = 1.0f, Y = 1.0f, Z = 1.0f, W = 1.0f };

        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public Vector4 ToVector4()
        {
            return new Vector4(X, Y, Z, W);
        }
    }

    private sealed class SerializedTexture2DRef
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;
        public SerializedTexture2DVariant Variant { get; set; } = new();
        public Texture2DSourceFormat SourceFormat { get; set; } = Texture2DSourceFormat.PpmP3;

        public Texture2DAsset ToTextureAsset()
        {
            return new Texture2DAsset(
                Guid,
                string.IsNullOrWhiteSpace(Name) ? Guid.ToString("N") : Name,
                Variant.ToVariantKey(),
                SourceFormat);
        }
    }

    private sealed class SerializedTexture2DVariant
    {
        public Texture2DCookedFormat Format { get; set; } = Texture2DCookedFormat.R8G8B8A8UNorm;
        public Texture2DColorSpace ColorSpace { get; set; } = Texture2DColorSpace.SRgb;
        public bool GenerateMipMaps { get; set; }

        public Texture2DVariantKey ToVariantKey()
        {
            return new Texture2DVariantKey(Format, ColorSpace, GenerateMipMaps);
        }
    }

    private sealed class SerializedMaterialRenderState
    {
        public string CullMode { get; set; } = "None";
        public string FrontFace { get; set; } = "CounterClockwise";
        public SerializedMaterialBlendState Blend { get; set; } = new();

        public void Validate(string sourcePath)
        {
            _ = ParseCullMode(sourcePath, CullMode);
            _ = ParseFrontFace(sourcePath, FrontFace);
            Blend.Validate(sourcePath);
        }

        public MaterialRenderState ToRenderState(string sourcePath)
        {
            return new MaterialRenderState(
                ParseCullMode(sourcePath, CullMode),
                ParseFrontFace(sourcePath, FrontFace),
                Blend.Enabled,
                ParseBlendFactor(sourcePath, Blend.SrcColor, nameof(Blend.SrcColor)),
                ParseBlendFactor(sourcePath, Blend.DstColor, nameof(Blend.DstColor)),
                ParseBlendOp(sourcePath, Blend.ColorOp));
        }

        private static ECullModeFlagBits ParseCullMode(string sourcePath, string? value)
        {
            return Normalize(value) switch
            {
                "" or "none" or "off" or "cullmodenone" or "cullmodeoff" or "cullmodenonebit" or "cull_mode_none" => ECullModeFlagBits.CULL_MODE_NONE,
                "front" or "cullmodefront" or "cullmodefrontbit" or "cull_mode_front_bit" => ECullModeFlagBits.CULL_MODE_FRONT_BIT,
                "back" or "cullmodeback" or "cullmodebackbit" or "cull_mode_back_bit" => ECullModeFlagBits.CULL_MODE_BACK_BIT,
                "frontandback" or "frontback" or "cullmodefrontandback" or "cull_mode_front_and_back" => ECullModeFlagBits.CULL_MODE_FRONT_AND_BACK,
                _ => throw new InvalidOperationException(
                    $"[MaterialAssetLoader] Material '{sourcePath}' RenderState.CullMode value '{value}' is unsupported.")
            };
        }

        private static EFrontFace ParseFrontFace(string sourcePath, string? value)
        {
            return Normalize(value) switch
            {
                "" or "counterclockwise" or "ccw" or "frontfacecounterclockwise" or "front_face_counter_clockwise" => EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE,
                "clockwise" or "cw" or "frontfaceclockwise" or "front_face_clockwise" => EFrontFace.FRONT_FACE_CLOCKWISE,
                _ => throw new InvalidOperationException(
                    $"[MaterialAssetLoader] Material '{sourcePath}' RenderState.FrontFace value '{value}' is unsupported.")
            };
        }

        public static EBlendFactor ParseBlendFactor(string sourcePath, string? value, string fieldName)
        {
            return Normalize(value) switch
            {
                "" or "zero" or "blendfactorzero" or "blend_factor_zero" => EBlendFactor.BLEND_FACTOR_ZERO,
                "one" or "blendfactorone" or "blend_factor_one" => EBlendFactor.BLEND_FACTOR_ONE,
                "srccolor" or "sourcecolor" or "blendfactorsrccolor" or "blend_factor_src_color" => EBlendFactor.BLEND_FACTOR_SRC_COLOR,
                "oneminussrccolor" or "oneminussourcecolor" or "blendfactoroneminussrccolor" or "blend_factor_one_minus_src_color" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_SRC_COLOR,
                "dstcolor" or "destinationcolor" or "blendfactordstcolor" or "blend_factor_dst_color" => EBlendFactor.BLEND_FACTOR_DST_COLOR,
                "oneminusdstcolor" or "oneminusdestinationcolor" or "blendfactoroneminusdstcolor" or "blend_factor_one_minus_dst_color" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_DST_COLOR,
                "srcalpha" or "sourcealpha" or "blendfactorsrcalpha" or "blend_factor_src_alpha" => EBlendFactor.BLEND_FACTOR_SRC_ALPHA,
                "oneminussrcalpha" or "oneminussourcealpha" or "blendfactoroneminussrcalpha" or "blend_factor_one_minus_src_alpha" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_SRC_ALPHA,
                "dstalpha" or "destinationalpha" or "blendfactordstalpha" or "blend_factor_dst_alpha" => EBlendFactor.BLEND_FACTOR_DST_ALPHA,
                "oneminusdstalpha" or "oneminusdestinationalpha" or "blendfactoroneminusdstalpha" or "blend_factor_one_minus_dst_alpha" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_DST_ALPHA,
                _ => throw new InvalidOperationException(
                    $"[MaterialAssetLoader] Material '{sourcePath}' RenderState.Blend.{fieldName} value '{value}' is unsupported.")
            };
        }

        public static EBlendOp ParseBlendOp(string sourcePath, string? value)
        {
            return Normalize(value) switch
            {
                "" or "add" or "blendopadd" or "blend_op_add" => EBlendOp.BLEND_OP_ADD,
                "subtract" or "sub" or "blendopsubtract" or "blend_op_subtract" => EBlendOp.BLEND_OP_SUBTRACT,
                "reversesubtract" or "revsub" or "blendopreversesubtract" or "blend_op_reverse_subtract" => EBlendOp.BLEND_OP_REVERSE_SUBTRACT,
                "min" or "blendopmin" or "blend_op_min" => EBlendOp.BLEND_OP_MIN,
                "max" or "blendopmax" or "blend_op_max" => EBlendOp.BLEND_OP_MAX,
                _ => throw new InvalidOperationException(
                    $"[MaterialAssetLoader] Material '{sourcePath}' RenderState.Blend.ColorOp value '{value}' is unsupported.")
            };
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim()
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        }
    }

    private sealed class SerializedMaterialBlendState
    {
        public bool Enabled { get; set; }
        public string SrcColor { get; set; } = "One";
        public string DstColor { get; set; } = "Zero";
        public string ColorOp { get; set; } = "Add";

        public void Validate(string sourcePath)
        {
            _ = SerializedMaterialRenderState.ParseBlendFactor(sourcePath, SrcColor, nameof(SrcColor));
            _ = SerializedMaterialRenderState.ParseBlendFactor(sourcePath, DstColor, nameof(DstColor));
            _ = SerializedMaterialRenderState.ParseBlendOp(sourcePath, ColorOp);
        }
    }
}

public static class MaterialAssetCooker
{
    private const string MaterialAssetType = "Material";
    private const string Variant = "material.runtime";
    private const int CurrentVersion = 5;
    private const int MaxStringByteCount = 16 * 1024;
    private const int MaxCollectionCount = 1024;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARISMATL");

    public static CookedMaterial LoadOrCook(IAssetDatabase assetDatabase, Guid materialGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (materialGuid == Guid.Empty)
        {
            throw new ArgumentException("[MaterialAssetCooker] Material GUID cannot be empty.", nameof(materialGuid));
        }

        if (!assetDatabase.TryGetAsset(materialGuid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[MaterialAssetCooker] Material asset '{materialGuid}' was not found.");
        }

        if (!string.Equals(sourceAsset.AssetType, MaterialAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[MaterialAssetCooker] Material asset '{materialGuid}' has asset type '{sourceAsset.AssetType}', expected '{MaterialAssetType}'.");
        }

        var outputPath = assetDatabase.GetCookedArtifactPath(materialGuid, Variant, ".material");
        var newestSourceWriteTimeUtc = GetNewestSourceWriteTimeUtc(assetDatabase, materialGuid, sourceAsset);

        if (!File.Exists(outputPath) ||
            File.GetLastWriteTimeUtc(outputPath) < newestSourceWriteTimeUtc ||
            !IsCurrentCookedMaterial(outputPath))
        {
            CookMaterial(assetDatabase, sourceAsset, materialGuid, outputPath);
        }

        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length <= s_Magic.Length + sizeof(int))
        {
            throw new InvalidOperationException(
                $"[MaterialAssetCooker] Material asset '{materialGuid}' produced no cooked payload.");
        }

        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            materialGuid,
            sourceAsset.AssetType,
            Variant,
            outputInfo.FullName,
            outputInfo.Length,
            outputInfo.LastWriteTimeUtc));

        if (!assetDatabase.TryLoadCookedAsset(materialGuid, Variant, MaterialAssetType, out var handle))
        {
            throw new InvalidOperationException(
                $"[MaterialAssetCooker] Failed to load cooked material asset '{materialGuid}'.");
        }

        try
        {
            var bytes = assetDatabase.GetCookedAssetBytes(handle);
            var material = ReadMaterial(bytes.Span);
            return new CookedMaterial(material, Variant, handle);
        }
        catch
        {
            assetDatabase.Release(handle);
            throw;
        }
    }

    private static DateTime GetNewestSourceWriteTimeUtc(
        IAssetDatabase assetDatabase,
        Guid materialGuid,
        AssetRecord sourceAsset)
    {
        var newest = File.GetLastWriteTimeUtc(sourceAsset.SourcePath);
        if (File.Exists(sourceAsset.MetaPath))
        {
            var metaWriteTimeUtc = File.GetLastWriteTimeUtc(sourceAsset.MetaPath);
            if (metaWriteTimeUtc > newest)
            {
                newest = metaWriteTimeUtc;
            }
        }

        var material = MaterialAssetLoader.LoadSource(assetDatabase, materialGuid);
        var materialStamp = AssetDependencyTracker.GetMaterialStamp(assetDatabase, material);
        if (materialStamp.IsValid)
        {
            var dependencyWriteTimeUtc = new DateTime(materialStamp.Value, DateTimeKind.Utc);
            if (dependencyWriteTimeUtc > newest)
            {
                newest = dependencyWriteTimeUtc;
            }
        }

        return newest;
    }

    private static void CookMaterial(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        Guid materialGuid,
        string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var material = MaterialAssetLoader.LoadSource(assetDatabase, materialGuid);
        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, CurrentVersion);
        WriteGuid(stream, material.Guid);
        WriteString(stream, material.Name);
        WriteShader(stream, material.Shader);

        var textureRefs = material.Texture2DRefs ?? Array.Empty<MaterialTexture2DRef>();
        WriteInt32(stream, textureRefs.Count);
        for (int i = 0; i < textureRefs.Count; i++)
        {
            WriteTextureRef(stream, textureRefs[i]);
        }

        var scalarProperties = material.ScalarProperties ?? Array.Empty<MaterialScalarProperty>();
        WriteInt32(stream, scalarProperties.Count);
        for (int i = 0; i < scalarProperties.Count; i++)
        {
            WriteScalarProperty(stream, scalarProperties[i]);
        }

        var vector4Properties = material.Vector4Properties ?? Array.Empty<MaterialVector4Property>();
        WriteInt32(stream, vector4Properties.Count);
        for (int i = 0; i < vector4Properties.Count; i++)
        {
            WriteVector4Property(stream, vector4Properties[i]);
        }

        WriteRenderState(stream, material.RenderState);

        Logger.Log(
            $"[MaterialAssetCooker] Cooked material asset {sourceAsset.Guid} | Textures: {textureRefs.Count} | ScalarProperties: {scalarProperties.Count} | Vector4Properties: {vector4Properties.Count} | RenderState: Cull={material.RenderState.CullMode}, Blend={material.RenderState.BlendEnabled} | Variant: {Variant} | Output: {outputPath}");
    }

    private static bool IsCurrentCookedMaterial(string outputPath)
    {
        try
        {
            using var stream = File.OpenRead(outputPath);
            Span<byte> header = stackalloc byte[s_Magic.Length + sizeof(int)];
            if (stream.Read(header) != header.Length)
            {
                return false;
            }

            if (!header.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
            {
                return false;
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(s_Magic.Length, sizeof(int)));
            return version == CurrentVersion;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteShader(Stream stream, ShaderAsset shader)
    {
        if (shader == null)
        {
            throw new InvalidOperationException("[MaterialAssetCooker] Material shader cannot be null.");
        }

        WriteGuid(stream, shader.Guid);
        WriteString(stream, shader.Name);
        WriteInt32(stream, (int)shader.Variant.Backend);
        WriteString(stream, shader.Variant.TargetEnvironment);
        WriteString(stream, shader.Variant.ShaderModel);
        WriteString(stream, shader.Variant.OptimizationLevel);
        WriteInt32(stream, shader.Variant.DebugInfo ? 1 : 0);

        var stages = shader.Stages ?? Array.Empty<ShaderStageAsset>();
        WriteInt32(stream, stages.Count);
        for (int i = 0; i < stages.Count; i++)
        {
            WriteString(stream, stages[i].Name);
            WriteInt32(stream, (int)stages[i].ProgramStage);
            WriteString(stream, stages[i].EntryPoint);
        }

        WriteStringList(stream, shader.Defines);
        WriteStringList(stream, shader.Includes);
        WriteStringList(stream, shader.VariantKeywords);
    }

    private static void WriteTextureRef(Stream stream, MaterialTexture2DRef textureRef)
    {
        WriteString(stream, textureRef.Name);
        WriteUInt32(stream, textureRef.Slot);
        WriteGuid(stream, textureRef.Texture.Guid);
        WriteString(stream, textureRef.Texture.Name);
        WriteInt32(stream, (int)textureRef.Texture.Variant.Format);
        WriteInt32(stream, (int)textureRef.Texture.Variant.ColorSpace);
        WriteInt32(stream, textureRef.Texture.Variant.GenerateMipMaps ? 1 : 0);
        WriteInt32(stream, (int)textureRef.Texture.SourceFormat);
    }

    private static void WriteScalarProperty(Stream stream, MaterialScalarProperty property)
    {
        WriteString(stream, property.Name);
        WriteSingle(stream, property.Value);
    }

    private static void WriteVector4Property(Stream stream, MaterialVector4Property property)
    {
        WriteString(stream, property.Name);
        WriteSingle(stream, property.Value.X);
        WriteSingle(stream, property.Value.Y);
        WriteSingle(stream, property.Value.Z);
        WriteSingle(stream, property.Value.W);
    }

    private static void WriteRenderState(Stream stream, MaterialRenderState renderState)
    {
        WriteInt32(stream, (int)renderState.CullMode);
        WriteInt32(stream, (int)renderState.FrontFace);
        WriteInt32(stream, renderState.BlendEnabled ? 1 : 0);
        WriteInt32(stream, (int)renderState.SrcColorBlendFactor);
        WriteInt32(stream, (int)renderState.DstColorBlendFactor);
        WriteInt32(stream, (int)renderState.ColorBlendOp);
    }

    private static MaterialAsset ReadMaterial(ReadOnlySpan<byte> bytes)
    {
        var reader = new MaterialPayloadReader(bytes);
        if (!reader.ReadBytes(s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException("[MaterialAssetCooker] Cooked material header magic is invalid.");
        }

        var version = reader.ReadInt32();
        if (version < 1 || version > CurrentVersion)
        {
            throw new InvalidOperationException($"[MaterialAssetCooker] Cooked material version '{version}' is not supported.");
        }

        var materialGuid = reader.ReadGuid();
        var materialName = reader.ReadString();
        var shader = ReadShader(ref reader, version);
        var textureCount = reader.ReadCount("Texture2DRefs");
        var textureRefs = new MaterialTexture2DRef[textureCount];
        for (int i = 0; i < textureRefs.Length; i++)
        {
            textureRefs[i] = ReadTextureRef(ref reader);
        }

        MaterialScalarProperty[] scalarProperties;
        if (version >= 3)
        {
            var scalarPropertyCount = reader.ReadCount("ScalarProperties");
            scalarProperties = new MaterialScalarProperty[scalarPropertyCount];
            for (int i = 0; i < scalarProperties.Length; i++)
            {
                scalarProperties[i] = ReadScalarProperty(ref reader);
            }
        }
        else
        {
            scalarProperties = Array.Empty<MaterialScalarProperty>();
        }

        MaterialVector4Property[] vector4Properties;
        if (version >= 2)
        {
            var vector4PropertyCount = reader.ReadCount("Vector4Properties");
            vector4Properties = new MaterialVector4Property[vector4PropertyCount];
            for (int i = 0; i < vector4Properties.Length; i++)
            {
                vector4Properties[i] = ReadVector4Property(ref reader);
            }
        }
        else
        {
            vector4Properties = Array.Empty<MaterialVector4Property>();
        }

        var renderState = version >= 4
            ? ReadRenderState(ref reader)
            : MaterialRenderState.Default;

        reader.EnsureFullyRead();
        return new MaterialAsset(materialGuid, materialName, shader, textureRefs, scalarProperties, vector4Properties, renderState);
    }

    private static ShaderAsset ReadShader(ref MaterialPayloadReader reader, int materialVersion)
    {
        var shaderGuid = reader.ReadGuid();
        var shaderName = reader.ReadString();
        var variant = new ShaderVariantKey(
            (ShaderAssetBackend)reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadInt32() != 0);

        var stageCount = reader.ReadCount("ShaderStages");
        var stages = new ShaderStageAsset[stageCount];
        for (int i = 0; i < stages.Length; i++)
        {
            stages[i] = new ShaderStageAsset(
                reader.ReadString(),
                (EProgramStage)reader.ReadInt32(),
                reader.ReadString());
        }

        var defines = reader.ReadStringArray("ShaderDefines");
        var includes = reader.ReadStringArray("ShaderIncludes");
        var keywords = materialVersion >= 5
            ? reader.ReadStringArray("ShaderKeywords")
            : Array.Empty<string>();
        return new ShaderAsset(
            shaderGuid,
            shaderName,
            stages,
            variant,
            defines.Length == 0 ? null : defines,
            includes.Length == 0 ? null : includes,
            keywords.Length == 0 ? null : keywords);
    }

    private static MaterialTexture2DRef ReadTextureRef(ref MaterialPayloadReader reader)
    {
        var name = reader.ReadString();
        var slot = reader.ReadUInt32();
        var textureGuid = reader.ReadGuid();
        var textureName = reader.ReadString();
        var textureVariant = new Texture2DVariantKey(
            (Texture2DCookedFormat)reader.ReadInt32(),
            (Texture2DColorSpace)reader.ReadInt32(),
            reader.ReadInt32() != 0);
        var sourceFormat = (Texture2DSourceFormat)reader.ReadInt32();

        return new MaterialTexture2DRef(
            name,
            new Texture2DAsset(textureGuid, textureName, textureVariant, sourceFormat),
            slot);
    }

    private static MaterialScalarProperty ReadScalarProperty(ref MaterialPayloadReader reader)
    {
        return new MaterialScalarProperty(
            reader.ReadString(),
            reader.ReadSingle());
    }

    private static MaterialVector4Property ReadVector4Property(ref MaterialPayloadReader reader)
    {
        return new MaterialVector4Property(
            reader.ReadString(),
            new Vector4(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()));
    }

    private static MaterialRenderState ReadRenderState(ref MaterialPayloadReader reader)
    {
        return new MaterialRenderState(
            (ECullModeFlagBits)reader.ReadInt32(),
            (EFrontFace)reader.ReadInt32(),
            reader.ReadInt32() != 0,
            (EBlendFactor)reader.ReadInt32(),
            (EBlendFactor)reader.ReadInt32(),
            (EBlendOp)reader.ReadInt32());
    }

    private static void WriteStringList(Stream stream, IReadOnlyList<string>? values)
    {
        var count = values?.Count ?? 0;
        WriteInt32(stream, count);
        if (values == null)
        {
            return;
        }

        for (int i = 0; i < values.Count; i++)
        {
            WriteString(stream, values[i]);
        }
    }

    private static void WriteGuid(Stream stream, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        stream.Write(bytes);
    }

    private static void WriteString(Stream stream, string? value)
    {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(stream, byteCount);
        if (byteCount == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteSingle(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private ref struct MaterialPayloadReader
    {
        private readonly ReadOnlySpan<byte> m_Bytes;
        private int m_Offset;

        public MaterialPayloadReader(ReadOnlySpan<byte> bytes)
        {
            m_Bytes = bytes;
            m_Offset = 0;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || m_Offset + count > m_Bytes.Length)
            {
                throw new InvalidOperationException("[MaterialAssetCooker] Cooked material payload is truncated.");
            }

            var result = m_Bytes.Slice(m_Offset, count);
            m_Offset += count;
            return result;
        }

        public int ReadInt32()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(sizeof(int)));
        }

        public uint ReadUInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));
        }

        public float ReadSingle()
        {
            return BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(sizeof(float)));
        }

        public Guid ReadGuid()
        {
            return new Guid(ReadBytes(16));
        }

        public string ReadString()
        {
            var byteCount = ReadInt32();
            if (byteCount < 0 || byteCount > MaxStringByteCount)
            {
                throw new InvalidOperationException($"[MaterialAssetCooker] Cooked material string byte count '{byteCount}' is invalid.");
            }

            if (byteCount == 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(ReadBytes(byteCount));
        }

        public int ReadCount(string label)
        {
            var count = ReadInt32();
            if (count < 0 || count > MaxCollectionCount)
            {
                throw new InvalidOperationException($"[MaterialAssetCooker] Cooked material {label} count '{count}' is invalid.");
            }

            return count;
        }

        public string[] ReadStringArray(string label)
        {
            var count = ReadCount(label);
            var values = new string[count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ReadString();
            }

            return values;
        }

        public void EnsureFullyRead()
        {
            if (m_Offset != m_Bytes.Length)
            {
                throw new InvalidOperationException("[MaterialAssetCooker] Cooked material payload contains trailing bytes.");
            }
        }
    }
}
