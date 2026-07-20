using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Rendering.Resources;

public sealed record ModelGeneratedOutputDiagnostic(
    string MetaPath,
    Guid Guid,
    string AssetType,
    Guid SourceGuid,
    string ChildKind,
    string ChildKey,
    string Message);

public sealed record ModelGeneratedOutputInspection(
    string OutputRoot,
    IReadOnlyList<ModelGeneratedOutputDiagnostic> OrphanedGeneratedChildren,
    IReadOnlyList<ModelGeneratedOutputDiagnostic> ForeignGeneratedChildren);

public sealed record ModelSourceReimportResult(
    ModelSourceDescriptor Model,
    GltfModelImportPlan Plan,
    GltfModelImportEmissionResult Emission,
    string OutputRoot,
    IReadOnlyList<ModelGeneratedOutputDiagnostic> OrphanedGeneratedChildren,
    IReadOnlyList<ModelGeneratedOutputDiagnostic> ForeignGeneratedChildren)
{
    public IReadOnlyList<Guid> GeneratedChildGuids =>
        Plan.GeneratedChildren.Select(child => child.Metadata.Guid).ToArray();
}

public static class ModelSourceReimporter
{
    public static ModelSourceReimportResult Reimport(IAssetDatabase assetDatabase, Guid modelGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetDatabase.TryGetAsset(modelGuid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[ModelSourceReimporter] Model asset '{modelGuid}' was not found.");
        }

        return Reimport(sourceAsset);
    }

    public static ModelSourceReimportResult Reimport(AssetRecord sourceAsset)
    {
        if (sourceAsset == null)
        {
            throw new ArgumentNullException(nameof(sourceAsset));
        }

        using var _ = Profiler.Zone("ModelSourceReimporter.Reimport");
        var model = ModelSourceAssetLoader.LoadSource(sourceAsset);
        var plan = ModelSourceAssetLoader.CreateGltfPlan(sourceAsset, model);
        var outputRoot = ValidateOutputRoot(sourceAsset, model);
        var preflight = InspectGeneratedOutput(outputRoot, sourceAsset.Guid, plan);
        if (preflight.ForeignGeneratedChildren.Count > 0)
        {
            throw new InvalidOperationException(
                $"[ModelSourceReimporter] Refusing to reimport '{sourceAsset.SourcePath}' because output root '{outputRoot}' contains {preflight.ForeignGeneratedChildren.Count} generated metadata file(s) from another source GUID.");
        }

        var outputDirectory = Path.GetDirectoryName(outputRoot);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException(
                $"[ModelSourceReimporter] Model output root '{outputRoot}' has no parent output directory.");
        }

        var emission = GltfModelImportEmitter.Emit(
            plan,
            model.ResolvedSourcePath,
            outputDirectory,
            ModelSourceAssetLoader.CreateEmissionSettings(model));
        var inspection = InspectGeneratedOutput(outputRoot, sourceAsset.Guid, plan);
        Profiler.PlotValue("ModelImport.OrphanedChildCount", inspection.OrphanedGeneratedChildren.Count);
        Profiler.PlotValue("ModelImport.ForeignChildCount", inspection.ForeignGeneratedChildren.Count);

        return new ModelSourceReimportResult(
            model,
            plan,
            emission,
            outputRoot,
            inspection.OrphanedGeneratedChildren,
            inspection.ForeignGeneratedChildren);
    }

    public static IReadOnlyList<Guid> InvalidateCookedOutputs(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        ModelSourceReimportResult result)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (sourceAsset == null)
        {
            throw new ArgumentNullException(nameof(sourceAsset));
        }

        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (sourceAsset.Guid == Guid.Empty ||
            result.Model.Guid != sourceAsset.Guid ||
            result.Plan.SourceGuid != sourceAsset.Guid)
        {
            throw new InvalidOperationException(
                "[ModelSourceReimporter] Reimport invalidation result does not belong to the selected model source asset.");
        }

        using var _ = Profiler.Zone("ModelSourceReimporter.InvalidateCookedOutputs");
        var invalidatedAssets = new List<AssetRecord>(result.Plan.GeneratedChildren.Count + 1);
        var visitedGuids = new HashSet<Guid>();
        AddInvalidatedAsset(sourceAsset, invalidatedAssets, visitedGuids);

        for (int i = 0; i < result.Plan.GeneratedChildren.Count; i++)
        {
            var child = result.Plan.GeneratedChildren[i];
            if (assetDatabase.TryGetAsset(child.Metadata.Guid, out var childAsset))
            {
                AddInvalidatedAsset(childAsset, invalidatedAssets, visitedGuids);
                continue;
            }

            var packageId = child.Metadata.Generated?.SourcePackageId;
            AddInvalidatedAsset(
                new AssetRecord(
                    child.Metadata.Guid,
                    child.Metadata.AssetType,
                    string.Empty,
                    string.Empty,
                    string.IsNullOrWhiteSpace(packageId) ? sourceAsset.PackageId : packageId),
                invalidatedAssets,
                visitedGuids);
        }

        var invalidatedGuids = new Guid[invalidatedAssets.Count];
        for (int i = 0; i < invalidatedAssets.Count; i++)
        {
            assetDatabase.InvalidateCookedAssets(invalidatedAssets[i].Guid);
        }

        for (int i = 0; i < invalidatedAssets.Count; i++)
        {
            var asset = invalidatedAssets[i];
            assetDatabase.NotifyAssetChanged(new AssetChangeEvent(
                AssetChangeKind.Changed,
                asset.Guid,
                asset.AssetType,
                asset.SourcePath,
                string.Empty,
                asset.PackageId));
            invalidatedGuids[i] = asset.Guid;
        }

        Profiler.PlotValue("ModelImport.InvalidatedAssetCount", invalidatedGuids.Length);
        return invalidatedGuids;
    }

    public static string ValidateOutputRoot(AssetRecord sourceAsset, ModelSourceDescriptor model)
    {
        if (sourceAsset == null)
        {
            throw new ArgumentNullException(nameof(sourceAsset));
        }

        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        var sourceAssetsRoot = FindContainingAssetsDirectory(sourceAsset.SourcePath);
        if (sourceAssetsRoot == null)
        {
            throw new InvalidOperationException(
                $"[ModelSourceReimporter] Model source '{sourceAsset.SourcePath}' must live under a package/workspace Assets root.");
        }

        var outputRoot = NormalizeFullPath(ModelSourceAssetLoader.ResolveOutputRoot(
            sourceAsset.SourcePath,
            model.Import.OutputRoot));
        if (ContainsPathSegment(outputRoot, ".arisen"))
        {
            throw new InvalidOperationException(
                $"[ModelSourceReimporter] Model output root cannot be under generated .arisen output: '{outputRoot}'.");
        }

        var assetsRoot = NormalizeFullPath(sourceAssetsRoot.FullName);
        if (outputRoot.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase) ||
            !IsSameOrChildPath(outputRoot, assetsRoot))
        {
            throw new InvalidOperationException(
                $"[ModelSourceReimporter] Model output root '{outputRoot}' must be a child directory of the same package/workspace Assets root as '{sourceAsset.SourcePath}'.");
        }

        return outputRoot;
    }

    public static ModelGeneratedOutputInspection InspectGeneratedOutput(
        AssetRecord sourceAsset,
        ModelSourceDescriptor model,
        GltfModelImportPlan plan)
    {
        return InspectGeneratedOutput(ValidateOutputRoot(sourceAsset, model), sourceAsset.Guid, plan);
    }

    public static ModelGeneratedOutputInspection InspectGeneratedOutput(
        string outputRoot,
        Guid sourceGuid,
        GltfModelImportPlan plan)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("Model output root cannot be empty.", nameof(outputRoot));
        }

        if (sourceGuid == Guid.Empty)
        {
            throw new ArgumentException("Model source GUID cannot be empty.", nameof(sourceGuid));
        }

        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var normalizedOutputRoot = NormalizeFullPath(outputRoot);
        var expectedGuids = new HashSet<Guid>(plan.GeneratedChildren.Select(child => child.Metadata.Guid));
        var orphans = new List<ModelGeneratedOutputDiagnostic>();
        var foreign = new List<ModelGeneratedOutputDiagnostic>();
        if (!Directory.Exists(normalizedOutputRoot))
        {
            return new ModelGeneratedOutputInspection(normalizedOutputRoot, orphans, foreign);
        }

        foreach (var metaPath in Directory.EnumerateFiles(normalizedOutputRoot, "*.meta", SearchOption.AllDirectories))
        {
            AssetMetadata metadata;
            try
            {
                metadata = SerializationUtil.Deserialize<AssetMetadata>(metaPath, serializeIfNotExist: false);
            }
            catch (Exception ex)
            {
                orphans.Add(new ModelGeneratedOutputDiagnostic(
                    metaPath,
                    Guid.Empty,
                    string.Empty,
                    Guid.Empty,
                    string.Empty,
                    string.Empty,
                    $"Could not read generated metadata: {ex.Message}"));
                continue;
            }

            if (metadata.Generated == null)
            {
                continue;
            }

            if (metadata.Generated.SourceGuid != sourceGuid)
            {
                foreign.Add(CreateDiagnostic(
                    metaPath,
                    metadata,
                    $"Generated by source {metadata.Generated.SourceGuid}, not selected model source {sourceGuid}."));
                continue;
            }

            if (!expectedGuids.Contains(metadata.Guid))
            {
                orphans.Add(CreateDiagnostic(
                    metaPath,
                    metadata,
                    "Generated child belongs to this model source but is not present in the current import plan."));
            }
        }

        return new ModelGeneratedOutputInspection(normalizedOutputRoot, orphans, foreign);
    }

    private static ModelGeneratedOutputDiagnostic CreateDiagnostic(
        string metaPath,
        AssetMetadata metadata,
        string message)
    {
        var generated = metadata.Generated;
        return new ModelGeneratedOutputDiagnostic(
            metaPath,
            metadata.Guid,
            metadata.AssetType,
            generated?.SourceGuid ?? Guid.Empty,
            generated?.ChildKind ?? string.Empty,
            generated?.ChildKey ?? string.Empty,
            message);
    }

    private static void AddInvalidatedAsset(
        AssetRecord asset,
        List<AssetRecord> invalidatedAssets,
        HashSet<Guid> visitedGuids)
    {
        if (asset.Guid == Guid.Empty || !visitedGuids.Add(asset.Guid))
        {
            return;
        }

        invalidatedAssets.Add(asset);
    }

    private static bool ContainsPathSegment(string path, string segment)
    {
        var normalized = NormalizeFullPath(path);
        var parts = normalized.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrChildPath(string path, string potentialParent)
    {
        var normalizedPath = NormalizeFullPath(path);
        var normalizedParent = NormalizeFullPath(potentialParent);

        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static DirectoryInfo? FindContainingAssetsDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directoryPath = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath)
            : Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        var directory = new DirectoryInfo(directoryPath);
        while (directory != null)
        {
            if (string.Equals(directory.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
