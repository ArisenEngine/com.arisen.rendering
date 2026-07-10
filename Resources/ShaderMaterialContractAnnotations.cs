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

        using var reader = new StringReader(source);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            ParseLine(sourcePath, lineNumber, line, textureRefs, scalarProperties, vector4Properties);
        }

        return new MaterialShaderContract(
            Deduplicate(sourcePath, "Texture2DRefs", textureRefs),
            Deduplicate(sourcePath, "ScalarProperties", scalarProperties),
            Deduplicate(sourcePath, "Vector4Properties", vector4Properties));
    }

    private static void ParseLine(
        string sourcePath,
        int lineNumber,
        string line,
        List<string> textureRefs,
        List<string> scalarProperties,
        List<string> vector4Properties)
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
        string name = annotation[(separator + 1)..].Trim().Trim('"', '\'');
        int trailingSeparator = name.IndexOfAny(new[] { ' ', '\t', '/', '*' });
        if (trailingSeparator >= 0)
        {
            name = name[..trailingSeparator].Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"[ShaderMaterialContractAnnotations] Shader '{sourcePath}' line {lineNumber} material contract annotation is missing a binding name.");
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
