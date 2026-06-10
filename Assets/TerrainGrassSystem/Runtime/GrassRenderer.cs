using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainGrassSystem
{
    // Core renderer. One of these is owned by each GrassTerrain.
    //
    // Architecture (baked path):
    //   Bake()  - called once on init and whenever grass data changes.
    //             Dispatches CSBake for every terrain tile; stores all placed
    //             blades in _bakedBlades (persistent GPU buffer). One-time GPU
    //             sync to read back the blade count.
    //
    //   Render() - called every frame. Dispatches the lightweight CSCull kernel
    //              over the entire baked list: frustum + distance + LOD + wind.
    //              Writes the visible subset into _bladesHigh / _bladesLow, then
    //              submits the two RenderMeshIndirect calls — unchanged from the
    //              original path.
    //
    // The legacy CSGenerate path (per-tile, per-frame) is kept in the compute
    // shader but is no longer called from C#.
    public sealed class GrassRenderer : System.IDisposable
    {
        // ---- Shader IDs ----
        static readonly int s_BladesHighId         = Shader.PropertyToID("_BladesHigh");
        static readonly int s_BladesLowId          = Shader.PropertyToID("_BladesLow");
        static readonly int s_IndirectArgsHighId   = Shader.PropertyToID("_IndirectArgsHigh");
        static readonly int s_IndirectArgsLowId    = Shader.PropertyToID("_IndirectArgsLow");
        static readonly int s_CountHighId          = Shader.PropertyToID("_CountHigh");
        static readonly int s_CountLowId           = Shader.PropertyToID("_CountLow");
        static readonly int s_IndexCountHighId     = Shader.PropertyToID("_IndexCountHigh");
        static readonly int s_IndexCountLowId      = Shader.PropertyToID("_IndexCountLow");
        static readonly int s_TerrainHeightmapId   = Shader.PropertyToID("_TerrainHeightmap");
        static readonly int s_GrassMaskId          = Shader.PropertyToID("_GrassMask");
        static readonly int s_ClumpNoiseId         = Shader.PropertyToID("_ClumpNoise");
        static readonly int s_GrassTypeId          = Shader.PropertyToID("_GrassType");
        static readonly int s_TileOriginSizeId     = Shader.PropertyToID("_TileOriginSize");
        static readonly int s_GridSizeCountsId     = Shader.PropertyToID("_GridSizeCounts");
        static readonly int s_TerrainOriginId      = Shader.PropertyToID("_TerrainOrigin");
        static readonly int s_TerrainSizeId        = Shader.PropertyToID("_TerrainSize");
        static readonly int s_CullParamsId         = Shader.PropertyToID("_CullParams");
        static readonly int s_CameraPosId          = Shader.PropertyToID("_CameraPos");
        static readonly int s_FrustumPlanesId      = Shader.PropertyToID("_GrassFrustumPlanes");
        static readonly int s_DisableFrustumCullId = Shader.PropertyToID("_DisableFrustumCull");
        static readonly int s_GrassBladesId        = Shader.PropertyToID("_GrassBlades");
        static readonly int s_GrassLodSegmentsId   = Shader.PropertyToID("_GrassLodSegments");
        static readonly int s_WindNoiseId          = Shader.PropertyToID("_GrassWindNoise");
        static readonly int s_WindParamsId         = Shader.PropertyToID("_GrassWindParams");
        static readonly int s_WindDirectionId      = Shader.PropertyToID("_GrassWindDirection");
        static readonly int s_WindEnabledId        = Shader.PropertyToID("_GrassWindEnabled");
        static readonly int s_ThinningParamsId     = Shader.PropertyToID("_ThinningParams");
        static readonly int s_BakedBladesId        = Shader.PropertyToID("_BakedBlades");
        static readonly int s_BakeCounterId        = Shader.PropertyToID("_BakeCounter");
        static readonly int s_BakedCapacityId      = Shader.PropertyToID("_BakedCapacity");

        readonly ComputeShader _compute;
        readonly Material      _highMaterial;
        readonly Material      _lowMaterial;
        readonly Mesh          _highMesh;
        readonly Mesh          _lowMesh;
        readonly int           _highIndexCount;
        readonly int           _lowIndexCount;
        readonly int           _bakeKernel;
        readonly int           _cullKernel;
        readonly int           _buildArgsKernel;

        // Per-frame visibility append buffers (written by CSCull, consumed by draw).
        readonly GraphicsBuffer _bladesHigh;
        readonly GraphicsBuffer _bladesLow;
        readonly GraphicsBuffer _argsHigh;
        readonly GraphicsBuffer _argsLow;
        readonly GraphicsBuffer _countHigh;
        readonly GraphicsBuffer _countLow;

        // Persistent bake buffer (written once by CSBake, read every frame by CSCull).
        readonly GraphicsBuffer _bakedBlades;
        readonly GraphicsBuffer _bakeCounter;
        readonly uint[]         _bakeCounterData  = new uint[1];
        readonly uint[]         _bakeCounterClear = new uint[1]; // always {0}

        // Number of blades stored in _bakedBlades. Set after Bake() via CPU readback.
        int _bakedCount;

        readonly GraphicsBuffer _grassType;

        readonly Vector4[]           _frustumPlanes = new Vector4[6];
        readonly MaterialPropertyBlock _mpbHigh = new();
        readonly MaterialPropertyBlock _mpbLow  = new();

        public Bounds            RenderBounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        public ShadowCastingMode ShadowMode   = ShadowCastingMode.On;
        public bool              ReceiveShadows = true;

        public GrassRenderer(
            ComputeShader compute,
            Material      highMaterial,
            Material      lowMaterial,
            GrassTerrainSettings settings)
        {
            _compute      = compute;
            _highMaterial = highMaterial;
            _lowMaterial  = lowMaterial;

            _highMesh = GrassBladeMesh.CreateHighLod();
            _lowMesh  = GrassBladeMesh.CreateLowLod();
            _highIndexCount = GrassBladeMesh.IndexCountFor(_highMesh);
            _lowIndexCount  = GrassBladeMesh.IndexCountFor(_lowMesh);

            _bakeKernel      = _compute.FindKernel("CSBake");
            _cullKernel      = _compute.FindKernel("CSCull");
            _buildArgsKernel = _compute.FindKernel("CSBuildArgs");

            _bladesHigh = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Append | GraphicsBuffer.Target.Counter,
                settings.MaxHighLodBlades, GrassBlade.SizeBytes);
            _bladesLow = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Append | GraphicsBuffer.Target.Counter,
                settings.MaxLowLodBlades, GrassBlade.SizeBytes);

            _argsHigh = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _argsLow = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                1, GraphicsBuffer.IndirectDrawIndexedArgs.size);

            _countHigh = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            _countLow  = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            _bakedBlades = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                settings.MaxBakedBlades, GrassBlade.SizeBytes);
            _bakeCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            _grassType = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                1, GrassTypeParamsGpu.SizeBytes);
        }

        readonly GrassTypeParamsGpu[] _grassTypeStaging = new GrassTypeParamsGpu[1];

        public void UploadGrassType(GrassType type)
        {
            _grassTypeStaging[0] = type != null ? type.ToGpu() : default;
            _grassType.SetData(_grassTypeStaging);
        }

        // Tile descriptor used by both BakeInput and FrameInput.
        public readonly struct Tile
        {
            public readonly Vector2 OriginXZ;
            public readonly Vector2 SizeXZ;
            public Tile(Vector2 origin, Vector2 size) { OriginXZ = origin; SizeXZ = size; }
        }

        // Input for the one-time bake. Contains everything needed for blade
        // generation but nothing camera-dependent.
        public struct BakeInput
        {
            public Texture TerrainHeightmap;
            public Texture GrassMask;
            public Texture ClumpNoise;
            public Vector3 TerrainOrigin;
            public Vector3 TerrainSize;
            public GrassTerrainSettings Settings;
            public int TilesX;
            public int TilesZ;
        }

        // Per-frame data the GrassTerrain hands over.
        public struct FrameInput
        {
            public Camera  Camera;
            public Texture ClumpNoise; // used as fallback for wind texture slot when wind is off
            public GrassTerrainSettings Settings;

            public bool    WindEnabled;
            public Texture WindNoise;
            public Vector4 WindParams;
            public Vector4 WindDirection;
        }

        // Bake all blades for the entire terrain into the persistent buffer.
        // This must be called before the first Render() and re-called whenever
        // the terrain data (mask, type, heightmap) changes.
        // Contains one blocking GPU readback — acceptable as a one-time cost.
        public void Bake(in BakeInput input)
        {
            if (input.TerrainHeightmap == null || input.GrassMask == null || input.ClumpNoise == null) return;
            if (input.Settings == null) return;

            // Reset the atomic counter before the dispatch.
            _bakeCounterClear[0] = 0u;
            _bakeCounter.SetData(_bakeCounterClear);

            BindBakeInputs(input);

            var settings = input.Settings;
            uint axis = (uint)settings.BladesPerTileAxis;
            const uint threadGroupSize = 8;
            uint groupCount = (axis + threadGroupSize - 1) / threadGroupSize;

            // Dispatch for EVERY tile (not just visible), so the baked buffer
            // covers the full terrain and CSCull can cull freely each frame.
            for (int z = 0; z < input.TilesZ; ++z)
            for (int x = 0; x < input.TilesX; ++x)
            {
                float ox = input.TerrainOrigin.x + x * settings.TileSize;
                float oz = input.TerrainOrigin.z + z * settings.TileSize;
                float sx = Mathf.Min(settings.TileSize, input.TerrainOrigin.x + input.TerrainSize.x - ox);
                float sz = Mathf.Min(settings.TileSize, input.TerrainOrigin.z + input.TerrainSize.z - oz);
                if (sx <= 0f || sz <= 0f) continue;

                _compute.SetVector(s_TileOriginSizeId, new Vector4(ox, oz, sx, sz));
                _compute.SetInts(s_GridSizeCountsId, settings.BladesPerTileAxis, settings.BladesPerTileAxis, 0, 0);
                _compute.Dispatch(_bakeKernel, (int)groupCount, (int)groupCount, 1);
            }

            if (Application.isPlaying)
            {
                // Play mode: one-time blocking readback so Render() dispatches
                // the exact number of groups needed (no wasted threads).
                _bakeCounter.GetData(_bakeCounterData);
                _bakedCount = Mathf.Min((int)_bakeCounterData[0], settings.MaxBakedBlades);
                Debug.Log($"[GrassRenderer] Baked {_bakedCount:N0} blades (capacity {settings.MaxBakedBlades:N0})");
            }
            else
            {
                // Edit mode: skip the GPU sync to avoid hitching while the user
                // drags sliders. Render() will dispatch for the full capacity and
                // CSCull reads the actual count from _BakeCounter[0] on the GPU.
                _bakedCount = settings.MaxBakedBlades;
            }
        }

        public void Render(in FrameInput input)
        {
            if (input.Camera == null) return;
            if (_bakedCount <= 0)
            {
                ResetArgsToZero();
                return;
            }

            // 1) Reset per-frame visibility append buffers.
            _bladesHigh.SetCounterValue(0);
            _bladesLow.SetCounterValue(0);

            // 2) Bind CSCull inputs and dispatch over the entire baked blade list.
            BindCullInputs(input);

            const int cullGroupSize = 64;
            int groups = (_bakedCount + cullGroupSize - 1) / cullGroupSize;
            _compute.Dispatch(_cullKernel, groups, 1, 1);

            // 3) Copy append-buffer counts into the uint buffers the args kernel reads.
            GraphicsBuffer.CopyCount(_bladesHigh, _countHigh, 0);
            GraphicsBuffer.CopyCount(_bladesLow,  _countLow,  0);

            // 4) Build indirect args on the GPU.
            _compute.SetBuffer(_buildArgsKernel, s_IndirectArgsHighId, _argsHigh);
            _compute.SetBuffer(_buildArgsKernel, s_IndirectArgsLowId,  _argsLow);
            _compute.SetBuffer(_buildArgsKernel, s_CountHighId,        _countHigh);
            _compute.SetBuffer(_buildArgsKernel, s_CountLowId,         _countLow);
            _compute.SetInt   (s_IndexCountHighId, _highIndexCount);
            _compute.SetInt   (s_IndexCountLowId,  _lowIndexCount);
            _compute.Dispatch(_buildArgsKernel, 1, 1, 1);

            // 5) Submit indirect draws — one per LOD.
            SubmitDraw(_highMesh, _highMaterial, _bladesHigh, _argsHigh, GrassBladeMesh.HighLodSegments, _mpbHigh);
            SubmitDraw(_lowMesh,  _lowMaterial,  _bladesLow,  _argsLow,  GrassBladeMesh.LowLodSegments,  _mpbLow);
        }

        // ---- Culling matrix helpers (unchanged from original) ----------------

        internal static Matrix4x4 CalculateCullingMatrix(Camera cam, float frustumPaddingDegrees)
        {
            var projection = cam.projectionMatrix;
            if (!cam.orthographic)
                projection = ExpandPerspectiveProjection(projection, frustumPaddingDegrees);
            return projection * cam.worldToCameraMatrix;
        }

        static Matrix4x4 ExpandPerspectiveProjection(Matrix4x4 projection, float paddingDegrees)
        {
            float paddingRad = Mathf.Clamp(paddingDegrees, 0f, 40f) * Mathf.Deg2Rad;
            if (paddingRad <= 1e-5f) return projection;
            projection.m00 = ExpandProjectionAxis(projection.m00, paddingRad);
            projection.m11 = ExpandProjectionAxis(projection.m11, paddingRad);
            return projection;
        }

        static float ExpandProjectionAxis(float projectionScale, float paddingRad)
        {
            float absScale = Mathf.Abs(projectionScale);
            if (absScale <= 1e-5f) return projectionScale;
            float sign = projectionScale < 0f ? -1f : 1f;
            float halfAngle = Mathf.Atan(1f / absScale);
            float paddedHalfAngle = Mathf.Min(halfAngle + paddingRad, 89f * Mathf.Deg2Rad);
            return sign / Mathf.Tan(paddedHalfAngle);
        }

        // ---- Compute input binding ------------------------------------------

        void BindBakeInputs(in BakeInput input)
        {
            var settings = input.Settings;

            _compute.SetBuffer (_bakeKernel, s_BakedBladesId,      _bakedBlades);
            _compute.SetBuffer (_bakeKernel, s_BakeCounterId,       _bakeCounter);
            _compute.SetInt    (s_BakedCapacityId,                  settings.MaxBakedBlades);
            _compute.SetBuffer (_bakeKernel, s_GrassTypeId,         _grassType);
            _compute.SetTexture(_bakeKernel, s_TerrainHeightmapId,  input.TerrainHeightmap);
            _compute.SetTexture(_bakeKernel, s_GrassMaskId,         input.GrassMask);
            _compute.SetTexture(_bakeKernel, s_ClumpNoiseId,        input.ClumpNoise);

            _compute.SetVector(s_TerrainOriginId,
                new Vector4(input.TerrainOrigin.x, input.TerrainOrigin.y, input.TerrainOrigin.z, 0f));
            _compute.SetVector(s_TerrainSizeId,
                new Vector4(input.TerrainSize.x, input.TerrainSize.y, input.TerrainSize.z, 0f));

            // _CullParams.w = clumpScale is needed by CSBake for noise UV.
            _compute.SetVector(s_CullParamsId, new Vector4(
                settings.ComputeMaxDistanceSq(),
                settings.HighLodDistance,
                settings.LodBlendBand,
                settings.ClumpScale));
        }

        void BindCullInputs(in FrameInput input)
        {
            var settings = input.Settings;
            var cam      = input.Camera;

            _compute.SetBuffer(_cullKernel, s_BakedBladesId,  _bakedBlades);
            _compute.SetBuffer(_cullKernel, s_BakeCounterId,  _bakeCounter); // CSCull reads counter on GPU
            _compute.SetBuffer(_cullKernel, s_BladesHighId,   _bladesHigh);
            _compute.SetBuffer(_cullKernel, s_BladesLowId,    _bladesLow);
            _compute.SetBuffer(_cullKernel, s_GrassTypeId,    _grassType);

            // Wind
            bool windOn = input.WindEnabled && input.WindNoise != null;
            _compute.SetInt(s_WindEnabledId, windOn ? 1 : 0);
            _compute.SetTexture(_cullKernel, s_WindNoiseId,
                windOn ? input.WindNoise : input.ClumpNoise);  // fall back to clump to avoid unbound slot
            _compute.SetVector(s_WindParamsId,    input.WindParams);
            _compute.SetVector(s_WindDirectionId, input.WindDirection);

            _compute.SetVector(s_CullParamsId, new Vector4(
                settings.ComputeMaxDistanceSq(),
                settings.HighLodDistance,
                settings.LodBlendBand,
                settings.ClumpScale));

            _compute.SetVector(s_ThinningParamsId, new Vector4(
                settings.ThinningStartDistance,
                Mathf.Clamp01(settings.ThinningStrength),
                0f, 0f));

            _compute.SetVector(s_CameraPosId, new Vector4(
                cam.transform.position.x,
                cam.transform.position.y,
                cam.transform.position.z,
                Time.time));

            var planes = GeometryUtility.CalculateFrustumPlanes(
                CalculateCullingMatrix(cam, settings.FrustumPaddingDegrees));
            for (int i = 0; i < 6; ++i)
                _frustumPlanes[i] = new Vector4(
                    planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            _compute.SetVectorArray(s_FrustumPlanesId, _frustumPlanes);

            _compute.SetInt(s_DisableFrustumCullId, Application.isPlaying ? 0 : 1);
        }

        void SubmitDraw(Mesh mesh, Material material, GraphicsBuffer blades, GraphicsBuffer args, int lodSegments, MaterialPropertyBlock mpb)
        {
            if (mesh == null || material == null) return;

            mpb.SetBuffer(s_GrassBladesId, blades);
            mpb.SetInt(s_GrassLodSegmentsId, lodSegments);

            var rp = new RenderParams(material)
            {
                worldBounds       = RenderBounds,
                shadowCastingMode = ShadowMode,
                receiveShadows    = ReceiveShadows,
                camera            = null,
                layer             = 0,
                matProps          = mpb,
            };

            Graphics.RenderMeshIndirect(rp, mesh, args, 1);
        }

        readonly uint[] _zeroArgs = new uint[5];
        void ResetArgsToZero()
        {
            _argsHigh.SetData(_zeroArgs);
            _argsLow.SetData(_zeroArgs);
        }

        public void Dispose()
        {
            _bladesHigh?.Dispose();
            _bladesLow?.Dispose();
            _argsHigh?.Dispose();
            _argsLow?.Dispose();
            _countHigh?.Dispose();
            _countLow?.Dispose();
            _bakedBlades?.Dispose();
            _bakeCounter?.Dispose();
            _grassType?.Dispose();

            if (Application.isPlaying)
            {
                if (_highMesh != null) Object.Destroy(_highMesh);
                if (_lowMesh  != null) Object.Destroy(_lowMesh);
            }
            else
            {
                if (_highMesh != null) Object.DestroyImmediate(_highMesh);
                if (_lowMesh  != null) Object.DestroyImmediate(_lowMesh);
            }
        }
    }
}
