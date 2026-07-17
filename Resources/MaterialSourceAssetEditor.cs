using System.Globalization;
using System.Numerics;
using YamlDotNet.RepresentationModel;

namespace ArisenEngine.Rendering.Resources;

public readonly record struct MaterialTextureSourceReference(
    Guid Guid,
    string Name,
    Texture2DSourceFormat SourceFormat);

public readonly record struct MaterialSourceEditResult(
    bool Success,
    string Diagnostic);

public static class MaterialSourceAssetEditor
{
    public static MaterialSourceEditResult UpdateTexture2DRef(
        string sourcePath,
        string bindingName,
        MaterialTextureSourceReference texture)
    {
        if (texture.Guid == Guid.Empty)
        {
            return Fail("Texture GUID must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(texture.Name))
        {
            return Fail("Texture name must not be empty.");
        }

        if (!Enum.IsDefined(texture.SourceFormat))
        {
            return Fail($"Texture source format '{texture.SourceFormat}' is unsupported.");
        }

        return UpdateNamedEntry(sourcePath, "Texture2DRefs", bindingName, entry =>
        {
            if (!TryGetMapping(entry, "Texture", out var textureNode))
            {
                return Fail($"Texture2DRefs binding '{bindingName}' has no Texture mapping.");
            }

            SetChild(textureNode, "Guid", new YamlScalarNode(texture.Guid.ToString("D")));
            SetChild(textureNode, "Name", new YamlScalarNode(texture.Name));
            SetChild(textureNode, "SourceFormat", new YamlScalarNode(texture.SourceFormat.ToString()));
            return Success($"Updated Texture2DRefs binding '{bindingName}'.");
        });
    }

    public static MaterialSourceEditResult UpdateScalarProperty(
        string sourcePath,
        string propertyName,
        float value)
    {
        if (!float.IsFinite(value))
        {
            return Fail("Scalar value must be finite.");
        }

        return UpdateNamedEntry(sourcePath, "ScalarProperties", propertyName, entry =>
        {
            SetChild(entry, "Value", CreateFloat(value));
            return Success($"Updated ScalarProperties binding '{propertyName}'.");
        });
    }

    public static MaterialSourceEditResult UpdateVector4Property(
        string sourcePath,
        string propertyName,
        Vector4 value)
    {
        if (!IsFinite(value))
        {
            return Fail("Vector4 value must be finite.");
        }

        return UpdateNamedEntry(sourcePath, "Vector4Properties", propertyName, entry =>
        {
            if (TryGetChild(entry, "Value", out var existingValue) && existingValue is not YamlMappingNode)
            {
                return Fail($"Vector4Properties binding '{propertyName}' Value must be a mapping.");
            }

            var valueNode = existingValue as YamlMappingNode ?? new YamlMappingNode();
            SetChild(valueNode, "X", CreateFloat(value.X));
            SetChild(valueNode, "Y", CreateFloat(value.Y));
            SetChild(valueNode, "Z", CreateFloat(value.Z));
            SetChild(valueNode, "W", CreateFloat(value.W));
            SetChild(entry, "Value", valueNode);
            return Success($"Updated Vector4Properties binding '{propertyName}'.");
        });
    }

    private static MaterialSourceEditResult UpdateNamedEntry(
        string sourcePath,
        string sectionName,
        string bindingName,
        Func<YamlMappingNode, MaterialSourceEditResult> update)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Fail("Material source path is empty.");
        }

        if (string.IsNullOrWhiteSpace(bindingName))
        {
            return Fail($"{sectionName} binding name is empty.");
        }

        if (!File.Exists(sourcePath))
        {
            return Fail($"Material source file is missing: {sourcePath}");
        }

        try
        {
            var stream = new YamlStream();
            using (var reader = File.OpenText(sourcePath))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return Fail($"Material '{sourcePath}' root must be a YAML mapping.");
            }

            if (!TryGetSequence(root, sectionName, out var section))
            {
                return Fail($"Material '{sourcePath}' has no {sectionName} sequence.");
            }

            YamlMappingNode? matchedEntry = null;
            for (var index = 0; index < section.Children.Count; index++)
            {
                if (section.Children[index] is not YamlMappingNode entry ||
                    !TryGetScalar(entry, "Name", out var name) ||
                    !string.Equals(name, bindingName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (matchedEntry != null)
                {
                    return Fail($"Material '{sourcePath}' {sectionName} contains duplicate binding '{bindingName}'.");
                }

                matchedEntry = entry;
            }

            if (matchedEntry == null)
            {
                return Fail($"Material '{sourcePath}' {sectionName} has no binding named '{bindingName}'.");
            }

            var result = update(matchedEntry);
            if (!result.Success)
            {
                return result;
            }

            SaveAtomically(stream, sourcePath);
            return result;
        }
        catch (Exception ex)
        {
            return Fail($"Failed to update material '{sourcePath}': {ex.Message}");
        }
    }

    private static void SaveAtomically(YamlStream stream, string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = new StreamWriter(tempPath, append: false))
            {
                stream.Save(writer, assignAnchors: false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool TryGetSequence(
        YamlMappingNode mapping,
        string key,
        out YamlSequenceNode sequence)
    {
        if (TryGetChild(mapping, key, out var node) && node is YamlSequenceNode typedSequence)
        {
            sequence = typedSequence;
            return true;
        }

        sequence = null!;
        return false;
    }

    private static bool TryGetMapping(
        YamlMappingNode mapping,
        string key,
        out YamlMappingNode childMapping)
    {
        if (TryGetChild(mapping, key, out var node) && node is YamlMappingNode typedMapping)
        {
            childMapping = typedMapping;
            return true;
        }

        childMapping = null!;
        return false;
    }

    private static bool TryGetScalar(
        YamlMappingNode mapping,
        string key,
        out string value)
    {
        if (TryGetChild(mapping, key, out var node) && node is YamlScalarNode scalar)
        {
            value = scalar.Value ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetChild(
        YamlMappingNode mapping,
        string key,
        out YamlNode child)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                child = pair.Value;
                return true;
            }
        }

        child = null!;
        return false;
    }

    private static void SetChild(YamlMappingNode mapping, string key, YamlNode value)
    {
        foreach (var existingKey in mapping.Children.Keys)
        {
            if (existingKey is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                mapping.Children[existingKey] = value;
                return;
            }
        }

        mapping.Add(new YamlScalarNode(key), value);
    }

    private static YamlScalarNode CreateFloat(float value)
    {
        return new YamlScalarNode(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static bool IsFinite(Vector4 value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               float.IsFinite(value.W);
    }

    private static MaterialSourceEditResult Success(string diagnostic)
    {
        return new MaterialSourceEditResult(true, $"[MaterialSourceAssetEditor] {diagnostic}");
    }

    private static MaterialSourceEditResult Fail(string diagnostic)
    {
        return new MaterialSourceEditResult(false, $"[MaterialSourceAssetEditor] {diagnostic}");
    }
}
