using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace TerrainGrassSystem
{
    // Attach to a GameObject that has a Unity Terrain component. The terrain's
    // heightmap is sampled by the compute shader to keep grass attached to the
    // surface; grass placement is masked by the user-supplied grass mask and
    // varied by the user-supplied clump noise.
    [DefaultExecutionOrder(10000)]
    [ExecuteAlways]
    [RequireComponent(typeof(Terrain))]
    [AddComponentMenu("TerrainGrassSystem/Grass/Grass Terrain")]
    public class GrassTerrain : MonoBehaviour
    {
        [Header("Assets")]
        public ComputeShader ComputeShader;
        [Tooltip("Material that uses the TerrainGrassSystem/Grass/Blade shader. Same material can be used for both LODs but a second one allows different tuning.")]
        public Material HighLodMaterial;
        public Material LowLodMaterial;

        [Header("Maps")]
        [Tooltip("RGBA terrain mask painted via the Paint Grass tool.\nR = placement / density (the only required channel).\nG = height multiplier (0..1); G<0.5 enables folded short-blade rendering.\nB = clamp / tufting (opt-in — only painted areas get tufts).\nA = reserved.")]
        public Texture2D GrassMask;
        [Tooltip("Seamless Perlin noise texture used to drive clumping (height/color/direction).")]
        public Texture2D ClumpNoise;

        [Header("Type")]
        [Tooltip("Single GrassType used across the whole terrain. Per-area variation is driven by the GrassMask channels and the clump noise.")]
        public GrassType Type;

        [Header("Settings")]
        public GrassTerrainSettings Settings;

        [Header("Override Camera")]
        [Tooltip("Play-mode only. Optional camera to drive LOD/culling instead of Camera.main. Ignored in edit mode — there the Scene View camera always wins so grass authoring follows the camera you control.")]
        public Camera OverrideCamera;

        Terrain        _terrain;
        GrassRenderer  _renderer;
        GrassWind      _wind;
        readonly List<GrassRenderer.Tile> _visibleTiles = new(256);
        readonly Plane[] _frustumPlaneBuffer = new Plane[6];

        static readonly List<GrassTerrain> s_ActiveTerrains = new(8);
        static int s_LastRenderPassFrame = -1000;

        internal static IReadOnlyList<GrassTerrain> ActiveTerrains => s_ActiveTerrains;
        internal static bool HasAnyRenderPassTerrain
        {
            get
            {
                for (int i = 0; i < s_ActiveTerrains.Count; ++i)
                {
                    var terrain = s_ActiveTerrains[i];
                    if (terrain != null && terrain.ShouldRenderFromRenderPass) return true;
                }
                return false;
            }
        }

        internal static bool HasAnyDepthOcclusionRenderPassTerrain
        {
            get
            {
                for (int i = 0; i < s_ActiveTerrains.Count; ++i)
                {
                    var terrain = s_ActiveTerrains[i];
                    if (terrain != null
                        && terrain.ShouldRenderFromRenderPass
                        && terrain.Settings.EnableDepthOcclusion)
                        return true;
                }
                return false;
            }
        }

        internal bool ShouldRenderFromRenderPass =>
            Application.isPlaying
            && Settings != null
            && Settings.RenderPath == GrassRenderPath.UniversalRenderPass;

        internal static void MarkRenderPassQueued()
        {
            s_LastRenderPassFrame = Time.frameCount;
        }

        static bool RenderPassWasQueuedRecently =>
            Time.frameCount - s_LastRenderPassFrame <= 1;

        // Cached terrain-derived state — refreshed when bounds change.
        Vector3 _terrainOrigin;
        Vector3 _terrainSize;
        int     _tilesX;
        int     _tilesZ;
        float   _tileSize;

        // Snapshot of inspector fields that affect buffer allocation. Used to
        // decide whether OnValidate should trigger a renderer rebuild — fields
        // like OverrideCamera/GrassMask/ClumpNoise/Type that are bound
        // per-frame don't need one.
        GrassTerrainSettings _validateSettings;
        ComputeShader        _validateCompute;
        Material             _validateHighMat;
        Material             _validateLowMat;

        void OnEnable()
        {
            _terrain = GetComponent<Terrain>();
            if (!s_ActiveTerrains.Contains(this)) s_ActiveTerrains.Add(this);
            EnsureRenderer();
            RebuildTileGrid();
            CaptureValidateSnapshot();
        }

        void OnDisable()
        {
            s_ActiveTerrains.Remove(this);
            _renderer?.Dispose();
            _renderer = null;
        }

        void OnValidate()
        {
            // OnValidate runs during serialization, where DestroyImmediate (used
            // by GrassRenderer.Dispose to free its procedural blade meshes) is
            // not allowed. Defer the teardown to the next editor tick.
            //
            // We only rebuild when fields that affect buffer allocation change.
            // Fields bound per-frame — OverrideCamera, GrassMask, ClumpNoise —
            // are skipped so assigning them does not cause a renderer rebuild.
#if UNITY_EDITOR
            if (!NeedsRebuildForChanges()) return;

            UnityEditor.EditorApplication.delayCall -= DisposeRendererDeferred;
            UnityEditor.EditorApplication.delayCall += DisposeRendererDeferred;
#endif
        }

#if UNITY_EDITOR
        bool NeedsRebuildForChanges()
        {
            bool changed = !ReferenceEquals(_validateSettings, Settings)
                        || !ReferenceEquals(_validateCompute,  ComputeShader)
                        || !ReferenceEquals(_validateHighMat,  HighLodMaterial)
                        || !ReferenceEquals(_validateLowMat,   LowLodMaterial);

            CaptureValidateSnapshot();
            return changed;
        }

        void DisposeRendererDeferred()
        {
            // The object may have been destroyed between OnValidate and the
            // editor's next tick.
            if (this == null) return;
            if (_renderer == null) return;

            _renderer.Dispose();
            _renderer = null;
        }
#endif

        void CaptureValidateSnapshot()
        {
            _validateSettings = Settings;
            _validateCompute  = ComputeShader;
            _validateHighMat  = HighLodMaterial;
            _validateLowMat   = LowLodMaterial;
        }

        void LateUpdate()
        {
            if (!ValidateConfig()) return;
            if (ShouldRenderFromRenderPass && RenderPassWasQueuedRecently) return;
            EnsureRenderer();
            RebuildTileGridIfDirty();

            // Re-push grass-type params every frame so edits on the GrassType
            // ScriptableObject are reflected live. Buffer is 80 bytes.
            _renderer.UploadGrassType(Type);

            var cam = ResolveCamera();
            if (cam == null) return;

            // Use our own projection*view matrix instead of cam.cullingMatrix —
            // URP can override cullingMatrix and on non-default aspect ratios it
            // may not match what is rendered. Computed once and shared with both
            // CollectVisibleTiles and the compute shader via FrameInput.
            GeometryUtility.CalculateFrustumPlanes(
                GrassRenderer.CalculateCullingMatrix(cam, Settings.FrustumPaddingDegrees), _frustumPlaneBuffer);

            CollectVisibleTiles(cam);

            // Wind is optional. Re-find when missing (it may be added/removed or
            // null right after a domain reload) so grass picks it up live.
            if (_wind == null) _wind = FindFirstObjectByType<GrassWind>();
            bool hasWind = _wind != null;

            _renderer.Render(new GrassRenderer.FrameInput
            {
                Camera           = cam,
                TerrainHeightmap = _terrain.terrainData.heightmapTexture,
                GrassMask        = GrassMask,
                ClumpNoise       = ClumpNoise,
                TerrainOrigin    = _terrainOrigin,
                TerrainSize      = _terrainSize,
                Settings         = Settings,
                VisibleTiles     = _visibleTiles,
                WindEnabled      = hasWind && _wind.ActiveNoise != null,
                WindNoise        = hasWind ? _wind.ActiveNoise : null,
                WindParams       = hasWind ? _wind.ActiveParams : Vector4.zero,
                WindDirection    = hasWind ? _wind.ActiveDirection : new Vector4(1f, 0f, 0f, 0f),
                FrustumPlanes    = _frustumPlaneBuffer,
            });
        }

        internal void RenderFromRenderPass(
            UnsafeCommandBuffer cmd,
            Camera camera,
            TextureHandle occlusionDepthTexture,
            Matrix4x4 depthViewMatrix,
            Matrix4x4 depthProjectionMatrix,
            Vector4 depthTexelSize)
        {
            if (!ShouldRenderFromRenderPass) return;
            if (!ValidateConfig()) return;
            if (camera == null) return;

            EnsureRenderer();
            RebuildTileGridIfDirty();
            if (_renderer == null) return;

            _renderer.RenderBounds = WorldBounds();
            _renderer.UploadGrassType(cmd, Type);

            GeometryUtility.CalculateFrustumPlanes(
                GrassRenderer.CalculateCullingMatrix(camera, Settings.FrustumPaddingDegrees), _frustumPlaneBuffer);

            CollectVisibleTiles(camera);

            if (_wind == null) _wind = FindFirstObjectByType<GrassWind>();
            bool hasWind = _wind != null;

            _renderer.Render(cmd, new GrassRenderer.FrameInput
            {
                Camera           = camera,
                TerrainHeightmap = _terrain.terrainData.heightmapTexture,
                GrassMask        = GrassMask,
                ClumpNoise       = ClumpNoise,
                TerrainOrigin    = _terrainOrigin,
                TerrainSize      = _terrainSize,
                Settings         = Settings,
                VisibleTiles     = _visibleTiles,
                WindEnabled      = hasWind && _wind.ActiveNoise != null,
                WindNoise        = hasWind ? _wind.ActiveNoise : null,
                WindParams       = hasWind ? _wind.ActiveParams : Vector4.zero,
                WindDirection    = hasWind ? _wind.ActiveDirection : new Vector4(1f, 0f, 0f, 0f),
                FrustumPlanes    = _frustumPlaneBuffer,
                DepthOcclusionEnabled = Settings.EnableDepthOcclusion && occlusionDepthTexture.IsValid(),
                DepthOcclusionViewMatrix = depthViewMatrix,
                DepthOcclusionViewProjectionMatrix = depthProjectionMatrix * depthViewMatrix,
                DepthOcclusionTexelSize = depthTexelSize,
                ZBufferParams = CalculateZBufferParams(camera),
            }, occlusionDepthTexture);
        }

        static Vector4 CalculateZBufferParams(Camera cam)
        {
            float near = Mathf.Max(0.0001f, cam.nearClipPlane);
            float far = Mathf.Max(near + 0.0001f, cam.farClipPlane);
            float farOverNear = far / near;

            if (SystemInfo.usesReversedZBuffer)
            {
                float x = farOverNear - 1f;
                return new Vector4(x, 1f, x / far, 1f / far);
            }

            float normalX = 1f - farOverNear;
            return new Vector4(normalX, farOverNear, normalX / far, farOverNear / far);
        }

        bool ValidateConfig()
        {
            return ComputeShader != null
                && HighLodMaterial != null && LowLodMaterial != null
                && GrassMask != null && ClumpNoise != null
                && Type != null
                && Settings != null
                && _terrain != null && _terrain.terrainData != null;
        }

        void EnsureRenderer()
        {
            if (_renderer != null || !ValidateConfig()) return;

            _renderer = new GrassRenderer(ComputeShader, HighLodMaterial, LowLodMaterial, Settings)
            {
                RenderBounds = WorldBounds(),
                ShadowMode   = UnityEngine.Rendering.ShadowCastingMode.On,
            };
            _renderer.UploadGrassType(Type);
        }

        void RebuildTileGrid()
        {
            if (_terrain == null || _terrain.terrainData == null || Settings == null) return;

            _terrainOrigin = _terrain.transform.position;
            _terrainSize   = _terrain.terrainData.size;
            _tileSize      = Settings.TileSize;
            _tilesX        = Mathf.Max(1, Mathf.CeilToInt(_terrainSize.x / _tileSize));
            _tilesZ        = Mathf.Max(1, Mathf.CeilToInt(_terrainSize.z / _tileSize));
        }

        void RebuildTileGridIfDirty()
        {
            if (_terrain == null || _terrain.terrainData == null || Settings == null) return;
            if (_tileSize != Settings.TileSize
                || _terrainOrigin != _terrain.transform.position
                || _terrainSize   != _terrain.terrainData.size)
            {
                RebuildTileGrid();
            }
        }

        Bounds WorldBounds()
        {
            var b = new Bounds(
                _terrain != null ? _terrain.transform.position + _terrainSize * 0.5f : Vector3.zero,
                _terrain != null ? _terrainSize : Vector3.one * 1000f);
            // Expand vertically a bit so tall blades aren't culled at the edge.
            b.Expand(new Vector3(0f, 4f, 0f));
            return b;
        }

        Camera ResolveCamera()
        {
#if UNITY_EDITOR
            // In edit mode the Scene View camera always wins — High LOD then
            // follows the camera the author is actually navigating. We don't
            // let OverrideCamera/Camera.main hijack this because authoring is
            // the entire point of edit-mode grass.
            if (!Application.isPlaying)
            {
                var sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null) return sv.camera;

                // Fallback: any open Scene View (lastActive can be null right
                // after a domain reload or before the user focuses a view).
                if (UnityEditor.SceneView.sceneViews != null)
                {
                    for (int i = 0; i < UnityEditor.SceneView.sceneViews.Count; ++i)
                    {
                        var view = UnityEditor.SceneView.sceneViews[i] as UnityEditor.SceneView;
                        if (view != null && view.camera != null) return view.camera;
                    }
                }
            }
#endif
            if (OverrideCamera != null) return OverrideCamera;
            return Camera.main;
        }

        // Visible-tile collection: bounding-box frustum test + max-distance test.
        // We compute each tile's vertical extents from the terrain's full
        // height range to avoid sampling the heightmap on the CPU. This
        // overestimates the box, which is fine for culling.
        void CollectVisibleTiles(Camera cam)
        {
            _visibleTiles.Clear();
            Vector3 camPos = cam.transform.position;
            float tileCullDistSq = Settings.TileCullDistance * Settings.TileCullDistance;

            for (int z = 0; z < _tilesZ; ++z)
            for (int x = 0; x < _tilesX; ++x)
            {
                float ox = _terrainOrigin.x + x * _tileSize;
                float oz = _terrainOrigin.z + z * _tileSize;
                float sx = Mathf.Min(_tileSize, _terrainOrigin.x + _terrainSize.x - ox);
                float sz = Mathf.Min(_tileSize, _terrainOrigin.z + _terrainSize.z - oz);
                if (sx <= 0f || sz <= 0f) continue;

                Vector3 boxCenter = new Vector3(ox + sx * 0.5f, _terrainOrigin.y + _terrainSize.y * 0.5f, oz + sz * 0.5f);
                Vector3 boxSize   = new Vector3(sx, _terrainSize.y, sz);

                // Distance: nearest edge to camera in XZ.
                float dx = Mathf.Max(0f, Mathf.Abs(camPos.x - boxCenter.x) - sx * 0.5f);
                float dz = Mathf.Max(0f, Mathf.Abs(camPos.z - boxCenter.z) - sz * 0.5f);
                if (dx * dx + dz * dz > tileCullDistSq) continue;

                // In edit mode (Scene View authoring) we keep every tile in
                // the distance radius, so grass is visible behind / beside
                // the camera while painting. Play mode does normal frustum
                // culling.
                if (Application.isPlaying &&
                    !GeometryUtility.TestPlanesAABB(_frustumPlaneBuffer, new Bounds(boxCenter, boxSize))) continue;

                _visibleTiles.Add(new GrassRenderer.Tile(new Vector2(ox, oz), new Vector2(sx, sz)));
            }
        }
    }

    public sealed class GrassRenderPassFeature : ScriptableRendererFeature
    {
        [SerializeField] RenderPassEvent _renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        [SerializeField] bool _renderSceneView = true;

        GrassRenderPass _pass;

        public override void Create()
        {
            _pass = new GrassRenderPass
            {
                renderPassEvent = _renderPassEvent,
                RenderSceneView = _renderSceneView,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!GrassTerrain.HasAnyRenderPassTerrain) return;

            _pass ??= new GrassRenderPass();
            _pass.renderPassEvent = _renderPassEvent;
            _pass.RenderSceneView = _renderSceneView;
            _pass.ConfigureInput(GrassTerrain.HasAnyDepthOcclusionRenderPassTerrain
                ? ScriptableRenderPassInput.Depth
                : ScriptableRenderPassInput.None);
            GrassTerrain.MarkRenderPassQueued();
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            _pass = null;
        }

        sealed class GrassRenderPass : ScriptableRenderPass
        {
            internal bool RenderSceneView;

            sealed class PassData
            {
                public TextureHandle ColorTexture;
                public TextureHandle DepthTexture;
                public TextureHandle OcclusionDepthTexture;
                public Camera Camera;
                public Matrix4x4 ViewMatrix;
                public Matrix4x4 ProjectionMatrix;
                public Vector4 DepthTexelSize;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                if (cameraData.camera == null) return;
                if (cameraData.isPreviewCamera) return;
                if (cameraData.isSceneViewCamera && !RenderSceneView) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var colorTexture = resourceData.activeColorTexture;
                var depthTexture = resourceData.activeDepthTexture;
                if (!colorTexture.IsValid() || !depthTexture.IsValid()) return;

                var occlusionDepthTexture = resourceData.cameraDepthTexture;
                using var builder = renderGraph.AddUnsafePass<PassData>("Terrain Grass Render Pass", out var passData);

                passData.ColorTexture = colorTexture;
                passData.DepthTexture = depthTexture;
                passData.OcclusionDepthTexture = occlusionDepthTexture;
                passData.Camera = cameraData.camera;
                passData.ViewMatrix = cameraData.GetViewMatrix();
                bool renderIntoTexture = !resourceData.isActiveTargetBackBuffer || cameraData.targetTexture != null;
                passData.ProjectionMatrix = GL.GetGPUProjectionMatrix(
                    cameraData.GetProjectionMatrix(),
                    renderIntoTexture);

                var depthInfo = renderGraph.GetRenderTargetInfo(
                    occlusionDepthTexture.IsValid() ? occlusionDepthTexture : depthTexture);
                int width = Mathf.Max(1, depthInfo.width);
                int height = Mathf.Max(1, depthInfo.height);
                passData.DepthTexelSize = new Vector4(1f / width, 1f / height, width, height);

                builder.UseTexture(colorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(depthTexture, AccessFlags.ReadWrite);
                if (occlusionDepthTexture.IsValid())
                    builder.UseTexture(occlusionDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(
                        data.ColorTexture, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                        data.DepthTexture, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);

                    var terrains = GrassTerrain.ActiveTerrains;
                    for (int i = 0; i < terrains.Count; ++i)
                    {
                        terrains[i]?.RenderFromRenderPass(
                            context.cmd,
                            data.Camera,
                            data.OcclusionDepthTexture,
                            data.ViewMatrix,
                            data.ProjectionMatrix,
                            data.DepthTexelSize);
                    }
                });
            }
        }
    }
}
