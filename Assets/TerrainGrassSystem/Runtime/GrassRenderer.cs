using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainGrassSystem
{
    // Core renderer. One of these is owned by each GrassTerrain.
    // - Allocates GPU buffers
    // - Each frame: resets counters, dispatches CSGenerate for visible tiles,
    //   populates indirect args, and submits two RenderMeshIndirect calls
    //   (one per LOD).
    //
    // The renderer does not read back from the GPU; everything is GPU-resident.
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
        static readonly int s_ThinningParamsId      = Shader.PropertyToID("_ThinningParams");

        readonly ComputeShader _compute;
        readonly Material      _highMaterial;
        readonly Material      _lowMaterial;
        readonly Mesh          _highMesh;
        readonly Mesh          _lowMesh;
        readonly int           _highIndexCount;
        readonly int           _lowIndexCount;
        readonly int           _generateKernel;
        readonly int           _buildArgsKernel;

        readonly GraphicsBuffer _bladesHigh;
        readonly GraphicsBuffer _bladesLow;
        readonly GraphicsBuffer _argsHigh;
        readonly GraphicsBuffer _argsLow;
        readonly GraphicsBuffer _countHigh;
        readonly GraphicsBuffer _countLow;
        readonly GraphicsBuffer _grassType;

        readonly Vector4[] _frustumPlanes = new Vector4[6];
        readonly MaterialPropertyBlock _mpbHigh = new();
        readonly MaterialPropertyBlock _mpbLow  = new();

        public Bounds RenderBounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        public ShadowCastingMode ShadowMode = ShadowCastingMode.On;
        public bool ReceiveShadows = true;

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

            _generateKernel  = _compute.FindKernel("CSGenerate");
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

            _grassType = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                1, GrassTypeParamsGpu.SizeBytes);
        }

        readonly GrassTypeParamsGpu[] _grassTypeStaging = new GrassTypeParamsGpu[1];

        // Safe to call every frame — the staging array is reused and the GPU
        // buffer is 80 bytes. Lets art tweaks on the GrassType ScriptableObject
        // show up live without rebuilding anything.
        public void UploadGrassType(GrassType type)
        {
            _grassTypeStaging[0] = type != null ? type.ToGpu() : default;
            _grassType.SetData(_grassTypeStaging);
        }

        // Tile descriptor — the GrassTerrain pushes a list per frame.
        public readonly struct Tile
        {
            public readonly Vector2 OriginXZ;
            public readonly Vector2 SizeXZ;
            public Tile(Vector2 origin, Vector2 size) { OriginXZ = origin; SizeXZ = size; }
        }

        // Per-frame data the GrassTerrain hands over.
        public struct FrameInput
        {
            public Camera Camera;
            public Texture TerrainHeightmap;
            public Texture GrassMask;
            public Texture ClumpNoise;
            public Vector3 TerrainOrigin;
            public Vector3 TerrainSize;
            public GrassTerrainSettings Settings;
            public IReadOnlyList<Tile> VisibleTiles;

            // Wind, sampled per-blade in the compute pass. WindEnabled is false
            // when no GrassWind component exists — blades then get zero sway.
            public bool    WindEnabled;
            public Texture WindNoise;
            public Vector4 WindParams;
            public Vector4 WindDirection;
        }

        public void Render(in FrameInput input)
        {
            if (input.Camera == null) return;
            if (input.TerrainHeightmap == null || input.GrassMask == null || input.ClumpNoise == null) return;
            if (input.VisibleTiles == null || input.VisibleTiles.Count == 0)
            {
                // Even with no tiles we must zero the indirect args, otherwise
                // we'd render the previous frame's instance count.
                ResetArgsToZero();
                return;
            }

            // 1) Reset blade counters.
            _bladesHigh.SetCounterValue(0);
            _bladesLow.SetCounterValue(0);

            // 2) Prepare globals shared across all tile dispatches.
            BindCommonComputeInputs(input);

            // 3) Per-tile compute dispatch.
            var settings = input.Settings;
            uint axis = (uint)settings.BladesPerTileAxis;
            const uint threadGroupSize = 8;
            uint groupCount = (axis + threadGroupSize - 1) / threadGroupSize;

            for (int i = 0; i < input.VisibleTiles.Count; ++i)
            {
                var tile = input.VisibleTiles[i];
                _compute.SetVector(s_TileOriginSizeId,
                    new Vector4(tile.OriginXZ.x, tile.OriginXZ.y, tile.SizeXZ.x, tile.SizeXZ.y));
                _compute.SetInts(s_GridSizeCountsId,
                    settings.BladesPerTileAxis, settings.BladesPerTileAxis, 0, 0);
                _compute.Dispatch(_generateKernel, (int)groupCount, (int)groupCount, 1);
            }

            // 4) Copy append-buffer counts into the small uint buffers the args
            //    kernel reads.
            GraphicsBuffer.CopyCount(_bladesHigh, _countHigh, 0);
            GraphicsBuffer.CopyCount(_bladesLow,  _countLow,  0);

            // 5) Build indirect args on the GPU.
            _compute.SetBuffer(_buildArgsKernel, s_IndirectArgsHighId, _argsHigh);
            _compute.SetBuffer(_buildArgsKernel, s_IndirectArgsLowId,  _argsLow);
            _compute.SetBuffer(_buildArgsKernel, s_CountHighId,        _countHigh);
            _compute.SetBuffer(_buildArgsKernel, s_CountLowId,         _countLow);
            _compute.SetInt   (s_IndexCountHighId, _highIndexCount);
            _compute.SetInt   (s_IndexCountLowId,  _lowIndexCount);
            _compute.Dispatch(_buildArgsKernel, 1, 1, 1);

            // 6) Submit indirect draws — one per LOD.
            SubmitDraw(_highMesh, _highMaterial, _bladesHigh, _argsHigh, GrassBladeMesh.HighLodSegments, _mpbHigh);
            SubmitDraw(_lowMesh,  _lowMaterial,  _bladesLow,  _argsLow,  GrassBladeMesh.LowLodSegments,  _mpbLow);
        }

        void BindCommonComputeInputs(in FrameInput input)
        {
            var settings = input.Settings;
            var cam = input.Camera;

            // Compute-kernel resources.
            _compute.SetBuffer (_generateKernel, s_BladesHighId,       _bladesHigh);
            _compute.SetBuffer (_generateKernel, s_BladesLowId,        _bladesLow);
            _compute.SetBuffer (_generateKernel, s_GrassTypeId,        _grassType);
            _compute.SetTexture(_generateKernel, s_TerrainHeightmapId, input.TerrainHeightmap);
            _compute.SetTexture(_generateKernel, s_GrassMaskId,        input.GrassMask);
            _compute.SetTexture(_generateKernel, s_ClumpNoiseId,       input.ClumpNoise);

            // Wind (sampled per-blade in CSGenerate). Always bind a valid texture
            // to the slot — fall back to the clump noise when wind is off so the
            // dispatch never has an unbound resource, even though the sample is
            // gated out by _GrassWindEnabled.
            bool windOn = input.WindEnabled && input.WindNoise != null;
            _compute.SetInt(s_WindEnabledId, windOn ? 1 : 0);
            _compute.SetTexture(_generateKernel, s_WindNoiseId, windOn ? input.WindNoise : input.ClumpNoise);
            _compute.SetVector(s_WindParamsId,    input.WindParams);
            _compute.SetVector(s_WindDirectionId, input.WindDirection);

            _compute.SetVector(s_TerrainOriginId,
                new Vector4(input.TerrainOrigin.x, input.TerrainOrigin.y, input.TerrainOrigin.z, 0f));
            _compute.SetVector(s_TerrainSizeId,
                new Vector4(input.TerrainSize.x, input.TerrainSize.y, input.TerrainSize.z, 0f));

            _compute.SetVector(s_CullParamsId, new Vector4(
                settings.ComputeMaxDistanceSq(),
                settings.HighLodDistance,
                settings.LodBlendBand,
                settings.ClumpScale));

            // Distance thinning. x = start distance, y = strength (0..1). The
            // ramp ends at the max blade distance (sqrt of _CullParams.x in the
            // shader), so beyond `start` low-LOD blades drop out toward the
            // horizon. High LOD is never thinned.
            _compute.SetVector(s_ThinningParamsId, new Vector4(
                settings.ThinningStartDistance,
                Mathf.Clamp01(settings.ThinningStrength),
                0f, 0f));

            _compute.SetVector(s_CameraPosId, new Vector4(
                cam.transform.position.x,
                cam.transform.position.y,
                cam.transform.position.z,
                Time.time));

            // Frustum planes in (n.xyz, d) form. Use the actual projection*view
            // matrix — see GrassTerrain.CollectVisibleTiles for why
            // cam.cullingMatrix is unreliable on non-default aspect ratios.
            var planes = GeometryUtility.CalculateFrustumPlanes(
                cam.projectionMatrix * cam.worldToCameraMatrix);
            for (int i = 0; i < 6; ++i)
            {
                _frustumPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            }
            _compute.SetVectorArray(s_FrustumPlanesId, _frustumPlanes);

            // In edit mode (Scene View only) we generate blades regardless of
            // frustum so authors can see grass behind / beside the camera
            // while painting. In play mode we always cull.
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

        // Zero the args so a stale instanceCount cannot leak into a frame with no tiles.
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
