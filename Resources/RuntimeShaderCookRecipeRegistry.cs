using System.Text;
using ArisenEngine.Core.Assets;

namespace ArisenEngine.Rendering;

public sealed record RuntimeShaderCookRecipe(
    ShaderAsset Shader,
    string StageName,
    string OwnerId);

public interface IRuntimeShaderCookRecipeRegistry
{
    void RegisterRecipe(ShaderAsset shader, string stageName, string ownerId);

    bool TryGetRecipe(Guid shaderGuid, string variant, out RuntimeShaderCookRecipe recipe);
}

public sealed class RuntimeShaderCookRecipeRegistry : IRuntimeShaderCookRecipeRegistry
{
    private readonly Dictionary<RuntimeAssetIdentity, RecipeEntry> m_Recipes = new();

    public void RegisterRecipe(ShaderAsset shader, string stageName, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (shader.Guid == Guid.Empty)
        {
            throw Invalid("A shader recipe cannot use an empty GUID.");
        }

        ValidateText(stageName, "stage name");
        ValidateText(ownerId, "owner id");
        ShaderStageAsset stage = shader.Stages.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, stageName, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid(
                $"Shader '{shader.Guid:D}' does not declare stage '{stageName}'.");
        string variant = shader.Variant.GetCookedVariant(
            stage.EntryPoint,
            shader.VariantKeywords);
        var identity = new RuntimeAssetIdentity(shader.Guid, variant);
        string signature = BuildSignature(shader, stage);
        if (m_Recipes.TryGetValue(identity, out RecipeEntry? existing))
        {
            if (!string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Shader recipe '{identity}' is claimed by '{existing.Recipe.OwnerId}' and " +
                    $"'{ownerId}' with different stage, variant, define, or include inputs.");
            }

            return;
        }

        m_Recipes.Add(
            identity,
            new RecipeEntry(new RuntimeShaderCookRecipe(shader, stage.Name, ownerId), signature));
    }

    public bool TryGetRecipe(
        Guid shaderGuid,
        string variant,
        out RuntimeShaderCookRecipe recipe)
    {
        if (shaderGuid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(variant) &&
            m_Recipes.TryGetValue(new RuntimeAssetIdentity(shaderGuid, variant), out RecipeEntry? entry))
        {
            recipe = entry.Recipe;
            return true;
        }

        recipe = null!;
        return false;
    }

    private static string BuildSignature(ShaderAsset shader, ShaderStageAsset stage)
    {
        var builder = new StringBuilder(256);
        Append(builder, stage.Name);
        Append(builder, ((int)stage.ProgramStage).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, stage.EntryPoint);
        Append(builder, ((int)shader.Variant.Backend).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, shader.Variant.TargetEnvironment);
        Append(builder, shader.Variant.ShaderModel);
        Append(builder, shader.Variant.OptimizationLevel);
        Append(builder, shader.Variant.DebugInfo ? "1" : "0");
        AppendList(builder, shader.Defines);
        AppendList(builder, shader.Includes);
        AppendList(builder, ShaderVariantKey.NormalizeKeywordSet(shader.VariantKeywords));
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<string>? values)
    {
        int count = values?.Count ?? 0;
        Append(builder, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int index = 0; index < count; index++)
        {
            Append(builder, values![index] ?? string.Empty);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private static void ValidateText(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw Invalid($"The {context} must be non-empty canonical text.");
        }
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException($"[RuntimeShaderCookRecipeRegistry] {message}");
    }

    private sealed record RecipeEntry(
        RuntimeShaderCookRecipe Recipe,
        string Signature);
}
