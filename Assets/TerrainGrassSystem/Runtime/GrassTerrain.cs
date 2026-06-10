using UnityEngine;

namespace TerrainGrassSystem
{
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

        // Cached terrain-derived state — refreshed when bounds change.
        Vector3 _terrainOrigin;
        Vector3 _terrainSize;
        int     _tilesX;
        int     _tilesZ;
        float   _tileSize;

        // Bake dirty flag — set on init and whenever grass data changes.
        bool _bakeDirty;

        // Snapshot of inspector references that trigger a bake when they change.
        Texture2D         _bakeSnapshotMask;
        Texture2D         _bakeSnapshotNoise;
        GrassType         _bakeSnapshotType;
        GrassTypeParamsGpu _bakeSnapshotTypeParams; // value-compare to catch SO field edits

        // Snapshot of inspector fields that affect buffer allocation.
        GrassTerrainSettings _validateSettings;
        ComputeShader        _validateCompute;
        Material             _validateHighMat;
        Material             _validateLowMat;

        void OnEnable()
        {
            _terrain = GetComponent<Terrain>();
            EnsureRenderer();
            RebuildTileGrid();
            CaptureValidateSnapshot();
            MarkBakeDirty();
        }

        void OnDisable()
        {
            _renderer?.Dispose();
            _renderer = null;
        }

        void OnValidate()
        {
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
            if (this == null) return;
            if (_renderer == null) return;

            _renderer.Dispose();
            _renderer = null;
        }

        // Manual re-bake from the component context menu.
        [ContextMenu("Rebake Grass")]
        void RebakeGrass() => MarkBakeDirty();
#endif

        void CaptureValidateSnapshot()
        {
            _validateSettings = Settings;
            _validateCompute  = ComputeShader;
            _validateHighMat  = HighLodMaterial;
            _validateLowMat   = LowLodMaterial;
        }

        public void MarkBakeDirty()
        {
            _bakeDirty = true;
        }

        void LateUpdate()
        {
            if (!ValidateConfig()) return;
            EnsureRenderer();
            RebuildTileGridIfDirty();

            _renderer.UploadGrassType(Type);

            // Detect changes to bake-relevant assets and trigger a re-bake.
            // Reference checks catch asset swaps; value check on GrassType params
            // catches inspector field edits (height, color, density, etc.) on the
            // same SO without needing a separate OnValidate callback.
            if (!ReferenceEquals(_bakeSnapshotMask,  GrassMask)  ||
                !ReferenceEquals(_bakeSnapshotNoise, ClumpNoise) ||
                !ReferenceEquals(_bakeSnapshotType,  Type)       ||
                (Type != null && !Type.ToGpu().Equals(_bakeSnapshotTypeParams)))
            {
                MarkBakeDirty();
            }

            if (_bakeDirty)
            {
                _renderer.UploadGrassType(Type); // ensure GPU type is fresh before bake
                _renderer.Bake(new GrassRenderer.BakeInput
                {
                    TerrainHeightmap = _terrain.terrainData.heightmapTexture,
                    GrassMask        = GrassMask,
                    ClumpNoise       = ClumpNoise,
                    TerrainOrigin    = _terrainOrigin,
                    TerrainSize      = _terrainSize,
                    Settings         = Settings,
                    TilesX           = _tilesX,
                    TilesZ           = _tilesZ,
                });
                _bakeDirty = false;
                CaptureBakeSnapshot();
            }

            var cam = ResolveCamera();
            if (cam == null) return;

            // Wind is optional; re-find when missing.
            if (_wind == null) _wind = FindFirstObjectByType<GrassWind>();
            bool hasWind = _wind != null;

            _renderer.Render(new GrassRenderer.FrameInput
            {
                Camera           = cam,
                ClumpNoise       = ClumpNoise,
                Settings         = Settings,
                WindEnabled      = hasWind && _wind.ActiveNoise != null,
                WindNoise        = hasWind ? _wind.ActiveNoise      : null,
                WindParams       = hasWind ? _wind.ActiveParams     : Vector4.zero,
                WindDirection    = hasWind ? _wind.ActiveDirection  : new Vector4(1f, 0f, 0f, 0f),
            });
        }

        void CaptureBakeSnapshot()
        {
            _bakeSnapshotMask        = GrassMask;
            _bakeSnapshotNoise       = ClumpNoise;
            _bakeSnapshotType        = Type;
            _bakeSnapshotTypeParams  = Type != null ? Type.ToGpu() : default;
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
            MarkBakeDirty(); // new renderer has no baked data yet
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
                MarkBakeDirty(); // tile layout changed → positions changed
            }
        }

        Bounds WorldBounds()
        {
            var b = new Bounds(
                _terrain != null ? _terrain.transform.position + _terrainSize * 0.5f : Vector3.zero,
                _terrain != null ? _terrainSize : Vector3.one * 1000f);
            b.Expand(new Vector3(0f, 4f, 0f));
            return b;
        }

        Camera ResolveCamera()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null) return sv.camera;

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

    }
}
