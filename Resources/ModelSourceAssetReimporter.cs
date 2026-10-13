using System;
using System.Collections.Generic;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering.Resources;

/// <summary>
/// Reimports glTF-backed model sources. Generated child identity embeds the owning package id, so a
/// model that changes owner has to be reimported for its scene, meshes, materials, and textures to
/// be re-keyed together with the assets that reference them.
/// </summary>
public sealed class ModelSourceAssetReimporter : IAssetSourceReimporter
{
    public const string SourceImporterId = "ArisenModelImporter";

    public string ImporterId => SourceImporterId;

    public string SourceAssetType => ModelSourceAssetLoader.ModelAssetType;

    public int ReimportAll(IAssetDatabase database, IReadOnlyList<AssetRecord> sources)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sources);

        int rekeyedChildren = 0;
        for (int index = 0; index < sources.Count; index++)
        {
            AssetRecord source = sources[index];
            ModelSourceReimportResult result = ModelSourceReimporter.Reimport(source);
            if (result.ForeignGeneratedChildren.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[ModelSourceAssetReimporter] Reimporting '{source.SourcePath}' found " +
                    $"{result.ForeignGeneratedChildren.Count} generated output(s) owned by another source.");
            }

            if (result.OrphanedGeneratedChildren.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[ModelSourceAssetReimporter] Reimporting '{source.SourcePath}' left " +
                    $"{result.OrphanedGeneratedChildren.Count} orphaned generated output(s).");
            }

            rekeyedChildren += result.GeneratedChildGuids.Count;
            Profiler.PlotValue("ModelImport.ReimportedChildCount", result.GeneratedChildGuids.Count);
        }

        return rekeyedChildren;
    }
}
