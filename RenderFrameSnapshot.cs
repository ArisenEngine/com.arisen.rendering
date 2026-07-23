using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Rendering;

/// <summary>
/// Immutable per-frame data consumed by RenderGraph setup and command-recording workers.
/// The pointed-to data is copied into FrameArena by RenderSubsystem before rendering starts.
/// </summary>
public readonly unsafe struct RenderFrameSnapshot
{
    public RenderFrameSnapshot(
        RHIDevice device,
        RHISwapChain swapChain,
        RHIImageHandle targetImage,
        bool targetImageRequiresInitialization,
        uint surfaceId,
        RenderOutputKind outputKind,
        uint frameIndex,
        float deltaTime,
        uint width,
        uint height,
        WorldPosition renderOrigin,
        Camera* cameraPtr,
        int cameraCount,
        DirectionalLight* directionalLightPtr,
        int directionalLightCount,
        PointLight* pointLightPtr,
        int pointLightCount,
        SpotLight* spotLightPtr,
        int spotLightCount,
        SceneEnvironment sceneEnvironment,
        int sceneEnvironmentCount,
        MeshDrawCommand* drawListPtr,
        int drawListCount,
        StaticMeshRenderItem* staticMeshItemPtr,
        int staticMeshItemCount)
    {
        Device = device;
        SwapChain = swapChain;
        TargetImage = targetImage;
        TargetImageRequiresInitialization = targetImageRequiresInitialization;
        SurfaceId = surfaceId;
        OutputKind = outputKind;
        FrameIndex = frameIndex;
        DeltaTime = deltaTime;
        Width = width;
        Height = height;
        RenderOrigin = renderOrigin;
        CameraPtr = cameraPtr;
        CameraCount = cameraCount;
        DirectionalLightPtr = directionalLightPtr;
        DirectionalLightCount = directionalLightCount;
        PointLightPtr = pointLightPtr;
        PointLightCount = pointLightCount;
        SpotLightPtr = spotLightPtr;
        SpotLightCount = spotLightCount;
        SceneEnvironment = sceneEnvironment;
        SceneEnvironmentCount = sceneEnvironmentCount;
        DrawListPtr = drawListPtr;
        DrawListCount = drawListCount;
        StaticMeshItemPtr = staticMeshItemPtr;
        StaticMeshItemCount = staticMeshItemCount;
    }

    public RHIDevice Device { get; }
    public RHISwapChain SwapChain { get; }
    public RHIImageHandle TargetImage { get; }
    public bool TargetImageRequiresInitialization { get; }
    public uint SurfaceId { get; }
    public RenderOutputKind OutputKind { get; }
    public uint FrameIndex { get; }
    public float DeltaTime { get; }
    public uint Width { get; }
    public uint Height { get; }
    public WorldPosition RenderOrigin { get; }
    public Camera* CameraPtr { get; }
    public int CameraCount { get; }
    public DirectionalLight* DirectionalLightPtr { get; }
    public int DirectionalLightCount { get; }
    public PointLight* PointLightPtr { get; }
    public int PointLightCount { get; }
    public SpotLight* SpotLightPtr { get; }
    public int SpotLightCount { get; }
    public SceneEnvironment SceneEnvironment { get; }
    public int SceneEnvironmentCount { get; }
    public MeshDrawCommand* DrawListPtr { get; }
    public int DrawListCount { get; }
    public StaticMeshRenderItem* StaticMeshItemPtr { get; }
    public int StaticMeshItemCount { get; }

    public ReadOnlySpan<Camera> Cameras => new(CameraPtr, CameraCount);
    public ReadOnlySpan<DirectionalLight> DirectionalLights => new(DirectionalLightPtr, DirectionalLightCount);
    public ReadOnlySpan<PointLight> PointLights => new(PointLightPtr, PointLightCount);
    public ReadOnlySpan<SpotLight> SpotLights => new(SpotLightPtr, SpotLightCount);
    public ReadOnlySpan<MeshDrawCommand> DrawList => new(DrawListPtr, DrawListCount);
    public ReadOnlySpan<StaticMeshRenderItem> StaticMeshItems => new(StaticMeshItemPtr, StaticMeshItemCount);
}
