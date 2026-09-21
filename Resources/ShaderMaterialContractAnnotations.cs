using System;
using System.Collections.Generic;
using System.IO;

namespace ArisenEngine.Rendering.Resources;

public static class ShaderMaterialContractAnnotations
{
    private const string AnnotationPrefix = "@arisen.material.";

    public static MaterialShaderContract ParseFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return MaterialShaderContract.Empty;
        }

        return Parse(File.ReadAllText(sourcePath), sourcePath);
    }

    public static MaterialShaderContract Parse(string source, string sourcePath = "<shader>")
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return MaterialShaderContract.Empty;
        }

        var textureRefs = new List<string>();
        var scalarProperties = new List<string>();
        var vector4Properties = new List<string>();
        var textureVariants = new List<MaterialShaderTextureVariantRequirement>();

        using var reader = new StringReader(source);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            ParseLine(
                sourcePath,
                lineNumber,
                line,
                textureRefs,
                scalarProperties,
                vector4Properties,
                textureVariants);
        }

        return new MaterialShaderContract(
            DeduplicateTexture2DRefs(textureRefs),
            Deduplicate(sourcePath, "ScalarProperties", scalarProperties),
            Deduplicate(sourcePath, "Vector4Properties", vector4Properties),
            DeduplicateTextureVariants(sourcePath, textureVariants));
    }

    private static IReadOnlyList<string> DeduplicateTexture2DRefs(List<string> names)
    {
        if (names.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Count; index++)
        {
            string name = names[index];
            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static void ParseLine(
        string sourcePath,
        int lineNumber,
        string line,
        List<string> textureRefs,
        List<string> scalarProperties,
        List<string> vector4Properties,
        List<MaterialShaderTextureVariantRequirement> textureVariants)
    {
        int annotationIndex = line.IndexOf(AnnotationPrefix, StringComparison.OrdinalIgnoreCase);
        if (annotationIndex < 0)
        {
            return;
        }

        string annotation = line[annotationIndex..].Trim();
        int separator = annotation.IndexOfAny(new[] { ' ', '\t', ':', '=' });
        if (separator <= AnnotationPrefix.Length)
        {
            throw new InvalidOperationException(
                $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} has an incomplete material contract annotation.");
        }

        string kind = annotation[AnnotationPrefix.Length..separator].Trim();
        string[] tokens = SplitArguments(annotation[(separator + 1)..]);
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException(
                $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} material contract annotation is missing a binding name.");
        }

        string name = tokens[0];
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} material contract annotation is missing a binding name.");
        }

        if (IsTextureVariantKind(kind))
        {
            if (tokens.Length != 2)
            {
                throw new InvalidOperationException(
                    $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} material contract " +
                    $"annotation '{kind} {name}' must declare exactly one binding name and one cooked texture variant.");
            }

            string variant = tokens[1];
            if (!Texture2DVariantKey.TryParseCookedVariant(
                    variant,
                    out Texture2DVariantKey parsedVariant))
            {
                throw new InvalidOperationException(
                    $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} material contract " +
                    $"annotation '{name}' declares invalid texture variant '{variant}'.");
            }

            if (!string.Equals(variant, parsedVariant.GetCookedVariant(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} material contract " +
                    $"annotation '{name}' variant '{variant}' is not canonical; use '{parsedVariant.GetCookedVariant()}'.");
            }

            textureRefs.Add(name);
            textureVariants.Add(new MaterialShaderTextureVariantRequirement(name, variant));
            return;
        }

        if (IsTextureKind(kind))
        {
            textureRefs.Add(name);
            return;
        }

        if (IsScalarKind(kind))
        {
            scalarProperties.Add(name);
            return;
        }

        if (IsVector4Kind(kind))
        {
            vector4Properties.Add(name);
            return;
        }

        throw new InvalidOperationException(
            $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} uses unsupported material contract kind '{kind}'.");
    }

    private static string[] SplitArguments(string arguments)
    {
        string[] rawTokens = arguments.Trim().Trim('"', '\'')
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>(rawTokens.Length);
        for (int index = 0; index < rawTokens.Length; index++)
        {
            string token = rawTokens[index];
            int trailingSeparator = token.IndexOfAny(new[] { '/', '*' });
            if (trailingSeparator >= 0)
            {
                token = token[..trailingSeparator];
            }

            token = token.Trim();
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens.ToArray();
    }

    private static IReadOnlyList<MaterialShaderTextureVariantRequirement> DeduplicateTextureVariants(
        string sourcePath,
        List<MaterialShaderTextureVariantRequirement> requirements)
    {
        if (requirements.Count == 0)
        {
            return Array.Empty<MaterialShaderTextureVariantRequirement>();
        }

        var result = new List<MaterialShaderTextureVariantRequirement>(requirements.Count);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < requirements.Count; index++)
        {
            MaterialShaderTextureVariantRequirement requirement = requirements[index];
            if (seen.TryGetValue(requirement.Name, out string? existingVariant))
            {
                if (!string.Equals(existingVariant, requirement.CookedVariant, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' material contract " +
                        $"Texture2DRefs contains conflicting variants for '{requirement.Name}'.");
                }

                continue;
            }

            seen.Add(requirement.Name, requirement.CookedVariant);
            result.Add(requirement);
        }

        return result;
    }

    private static IReadOnlyList<string> Deduplicate(string sourcePath, string sectionName, List<string> names)
    {
        if (names.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' material contract {sectionName} contains duplicate name '{name}'.");
            }

            result.Add(name);
        }

        return result;
    }

    private static bool IsTextureKind(string kind)
    {
        return string.Equals(kind, "texture2d", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "texture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextureVariantKind(string kind)
    {
        return string.Equals(kind, "texture2dvariant", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "texturevariant", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScalarKind(string kind)
    {
        return string.Equals(kind, "scalar", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "float", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVector4Kind(string kind)
    {
        return string.Equals(kind, "vector4", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "float4", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "color", StringComparison.OrdinalIgnoreCase);
    }
}
