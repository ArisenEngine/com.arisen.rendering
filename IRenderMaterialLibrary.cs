using ArisenEngine.Core.Assets;

namespace ArisenEngine.Rendering;

/// <summary>
/// Setup-time material registry consumed by scene/bootstrap code.
/// Render workers consume only compact material ids copied into draw commands.
/// </summary>
public interface IRenderMaterialLibrary
{
    uint DefaultMaterialID { get; }

    uint RegisterMaterial(Guid materialGuid);

    uint RegisterMaterial(AssetRef<MaterialSourceAsset> materialRef);

    bool TryGetMaterialID(Guid materialGuid, out uint materialId);

    bool TryGetMaterialID(AssetRef<MaterialSourceAsset> materialRef, out uint materialId);
}
