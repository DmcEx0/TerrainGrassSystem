using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

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
        static readonly int s_OcclusionDepthId     = Shader.PropertyToID("_GrassOcclusionDepthTexture");
        static readonly int s_OcclusionParamsId    = Shader.PropertyToID("_GrassDepthOcclusionParams");
        static readonly int s_OcclusionTexelSizeId = Shader.PropertyToID("_GrassDepthOcclusionTexelSize");
        static readonly int s_OcclusionViewId      = Shader.PropertyToID("_GrassDepthOcclusionView");
        static readonly int s_OcclusionViewProjId  = Shader.PropertyToID("_GrassDepthOcclusionViewProj");
        static readonly int s_ZBufferParamsId      = Shader.PropertyToID("_GrassZBufferParams");
        static readonly int s_RenderingLayerId     = Shader.PropertyToID("unity_RenderingLayer");
        static readonly int s_LightDataId          = Shader.PropertyToID("unity_LightData");
        const uint k_DefaultRenderingLayerMask = 0x00000001u;
        static readonly float s_DefaultRenderingLayerMaskFloat = Unity.Mathematics.math.asfloat(k_DefaultRenderingLayerMask);

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

        internal void UploadGrassType(UnsafeCommandBuffer cmd, GrassType type)
        {
            _grassTypeStaging[0] = type != null ? type.ToGpu() : default;
            cmd.SetBufferData(_grassType, _grassTypeStaging);
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

            // Frustum planes pre-computed by GrassTerrain so BindCommonComputeInputs
            // does not recompute the culling matrix a second time this frame.
            public Plane[] FrustumPlanes;

            // Optional camera-depth occlusion. Only the render-pass path can
            // provide same-frame opaque depth; the legacy LateUpdate path keeps
            // this disabled.
            public bool DepthOcclusionEnabled;
            public Matrix4x4 DepthOcclusionViewMatrix;
            public Matrix4x4 DepthOcclusionViewProjectionMatrix;
            public Vector4 DepthOcclusionTexelSize; // xy = 1/size, zw = size
            public Vector4 ZBufferParams;
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

        internal void Generate(UnsafeCommandBuffer cmd, in FrameInput input, TextureHandle occlusionDepthTexture)
        {
            if (input.Camera == null) return;
            if (input.TerrainHeightmap == null || input.GrassMask == null || input.ClumpNoise == null) return;
            if (input.VisibleTiles == null || input.VisibleTiles.Count == 0)
            {
                ResetArgsToZero(cmd);
                return;
            }

            cmd.SetBufferCounterValue(_bladesHigh, 0);
            cmd.SetBufferCounterValue(_bladesLow,  0);

            BindCommonComputeInputs(cmd, input, occlusionDepthTexture);

            var settings = input.Settings;
            uint axis = (uint)settings.BladesPerTileAxis;
            const uint threadGroupSize = 8;
            uint groupCount = (axis + threadGroupSize - 1) / threadGroupSize;

            for (int i = 0; i < input.VisibleTiles.Count; ++i)
            {
                var tile = input.VisibleTiles[i];
                cmd.SetComputeVectorParam(_compute, s_TileOriginSizeId,
                    new Vector4(tile.OriginXZ.x, tile.OriginXZ.y, tile.SizeXZ.x, tile.SizeXZ.y));
                cmd.SetComputeIntParams(_compute, s_GridSizeCountsId,
                    settings.BladesPerTileAxis, settings.BladesPerTileAxis, 0, 0);
                cmd.DispatchCompute(_compute, _generateKernel, (int)groupCount, (int)groupCount, 1);
            }

            cmd.CopyCounterValue(_bladesHigh, _countHigh, 0);
            cmd.CopyCounterValue(_bladesLow,  _countLow,  0);

            cmd.SetComputeBufferParam(_compute, _buildArgsKernel, s_IndirectArgsHighId, _argsHigh);
            cmd.SetComputeBufferParam(_compute, _buildArgsKernel, s_IndirectArgsLowId,  _argsLow);
            cmd.SetComputeBufferParam(_compute, _buildArgsKernel, s_CountHighId,        _countHigh);
            cmd.SetComputeBufferParam(_compute, _buildArgsKernel, s_CountLowId,         _countLow);
            cmd.SetComputeIntParam   (_compute, s_IndexCountHighId, _highIndexCount);
            cmd.SetComputeIntParam   (_compute, s_IndexCountLowId,  _lowIndexCount);
            cmd.DispatchCompute(_compute, _buildArgsKernel, 1, 1, 1);
        }

        internal void DrawExisting(RasterCommandBuffer cmd)
        {
            SubmitDraw(cmd, _highMesh, _highMaterial, _bladesHigh, _argsHigh, GrassBladeMesh.HighLodSegments, _mpbHigh);
            SubmitDraw(cmd, _lowMesh,  _lowMaterial,  _bladesLow,  _argsLow,  GrassBladeMesh.LowLodSegments,  _mpbLow);
        }

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

            var planes = input.FrustumPlanes;
            for (int i = 0; i < 6; ++i)
            {
                _frustumPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            }
            _compute.SetVectorArray(s_FrustumPlanesId, _frustumPlanes);

            // In edit mode (Scene View only) we generate blades regardless of
            // frustum so authors can see grass behind / beside the camera
            // while painting. In play mode we always cull.
            _compute.SetInt(s_DisableFrustumCullId, Application.isPlaying ? 0 : 1);

            _compute.SetTexture(_generateKernel, s_OcclusionDepthId, Texture2D.blackTexture);
            _compute.SetVector(s_OcclusionParamsId, Vector4.zero);
            _compute.SetVector(s_OcclusionTexelSizeId, Vector4.zero);
            _compute.SetMatrix(s_OcclusionViewId, Matrix4x4.identity);
            _compute.SetMatrix(s_OcclusionViewProjId, Matrix4x4.identity);
            _compute.SetVector(s_ZBufferParamsId, input.ZBufferParams);
        }

        void BindCommonComputeInputs(UnsafeCommandBuffer cmd, in FrameInput input, TextureHandle occlusionDepthTexture)
        {
            var settings = input.Settings;
            var cam = input.Camera;

            cmd.SetComputeBufferParam (_compute, _generateKernel, s_BladesHighId,       _bladesHigh);
            cmd.SetComputeBufferParam (_compute, _generateKernel, s_BladesLowId,        _bladesLow);
            cmd.SetComputeBufferParam (_compute, _generateKernel, s_GrassTypeId,        _grassType);
            cmd.SetComputeTextureParam(_compute, _generateKernel, s_TerrainHeightmapId, new RenderTargetIdentifier(input.TerrainHeightmap));
            cmd.SetComputeTextureParam(_compute, _generateKernel, s_GrassMaskId,        new RenderTargetIdentifier(input.GrassMask));
            cmd.SetComputeTextureParam(_compute, _generateKernel, s_ClumpNoiseId,       new RenderTargetIdentifier(input.ClumpNoise));

            bool windOn = input.WindEnabled && input.WindNoise != null;
            cmd.SetComputeIntParam(_compute, s_WindEnabledId, windOn ? 1 : 0);
            cmd.SetComputeTextureParam(_compute, _generateKernel, s_WindNoiseId,
                new RenderTargetIdentifier(windOn ? input.WindNoise : input.ClumpNoise));
            cmd.SetComputeVectorParam(_compute, s_WindParamsId,    input.WindParams);
            cmd.SetComputeVectorParam(_compute, s_WindDirectionId, input.WindDirection);

            cmd.SetComputeVectorParam(_compute, s_TerrainOriginId,
                new Vector4(input.TerrainOrigin.x, input.TerrainOrigin.y, input.TerrainOrigin.z, 0f));
            cmd.SetComputeVectorParam(_compute, s_TerrainSizeId,
                new Vector4(input.TerrainSize.x, input.TerrainSize.y, input.TerrainSize.z, 0f));

            cmd.SetComputeVectorParam(_compute, s_CullParamsId, new Vector4(
                settings.ComputeMaxDistanceSq(),
                settings.HighLodDistance,
                settings.LodBlendBand,
                settings.ClumpScale));

            cmd.SetComputeVectorParam(_compute, s_ThinningParamsId, new Vector4(
                settings.ThinningStartDistance,
                Mathf.Clamp01(settings.ThinningStrength),
                0f, 0f));

            cmd.SetComputeVectorParam(_compute, s_CameraPosId, new Vector4(
                cam.transform.position.x,
                cam.transform.position.y,
                cam.transform.position.z,
                Time.time));

            var planes = input.FrustumPlanes;
            for (int i = 0; i < 6; ++i)
            {
                _frustumPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            }
            cmd.SetComputeVectorArrayParam(_compute, s_FrustumPlanesId, _frustumPlanes);
            cmd.SetComputeIntParam(_compute, s_DisableFrustumCullId, Application.isPlaying ? 0 : 1);

            bool hasOcclusionDepth = occlusionDepthTexture.IsValid();
            if (hasOcclusionDepth)
                cmd.SetComputeTextureParam(_compute, _generateKernel, s_OcclusionDepthId, occlusionDepthTexture);
            else
                cmd.SetComputeTextureParam(_compute, _generateKernel, s_OcclusionDepthId, new RenderTargetIdentifier(Texture2D.blackTexture));
            cmd.SetComputeVectorParam(_compute, s_OcclusionParamsId, new Vector4(
                input.DepthOcclusionEnabled && hasOcclusionDepth ? 1f : 0f,
                settings.DepthOcclusionBias,
                settings.DepthOcclusionSampleRadiusPixels,
                settings.DepthOcclusionRadiusScale));
            cmd.SetComputeVectorParam(_compute, s_OcclusionTexelSizeId, input.DepthOcclusionTexelSize);
            cmd.SetComputeMatrixParam(_compute, s_OcclusionViewId, input.DepthOcclusionViewMatrix);
            cmd.SetComputeMatrixParam(_compute, s_OcclusionViewProjId, input.DepthOcclusionViewProjectionMatrix);
            cmd.SetComputeVectorParam(_compute, s_ZBufferParamsId, input.ZBufferParams);
        }

        void SubmitDraw(Mesh mesh, Material material, GraphicsBuffer blades, GraphicsBuffer args, int lodSegments, MaterialPropertyBlock mpb)
        {
            if (mesh == null || material == null) return;

            BindDrawProperties(mpb, blades, lodSegments);

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

        void SubmitDraw(RasterCommandBuffer cmd, Mesh mesh, Material material, GraphicsBuffer blades, GraphicsBuffer args, int lodSegments, MaterialPropertyBlock mpb)
        {
            if (mesh == null || material == null) return;

            int forwardPass = FindForwardPass(material);
            if (forwardPass < 0) return;

            BindDrawProperties(mpb, blades, lodSegments);
            cmd.DrawMeshInstancedIndirect(mesh, 0, material, forwardPass, args, 0, mpb);
        }

        static void BindDrawProperties(MaterialPropertyBlock mpb, GraphicsBuffer blades, int lodSegments)
        {
            mpb.SetBuffer(s_GrassBladesId, blades);
            mpb.SetInt(s_GrassLodSegmentsId, lodSegments);
            mpb.SetVector(s_RenderingLayerId, new Vector4(s_DefaultRenderingLayerMaskFloat, 0f, 0f, 0f));
            mpb.SetVector(s_LightDataId, new Vector4(1f, 1f, 1f, 0f));
        }

        static int FindForwardPass(Material material)
        {
            int pass = material.FindPass("Universal Forward");
            if (pass >= 0) return pass;

            pass = material.FindPass("UniversalForward");
            if (pass >= 0) return pass;

            pass = material.FindPass("Universal Forward Only");
            if (pass >= 0) return pass;

            pass = material.FindPass("UniversalForwardOnly");
            if (pass >= 0) return pass;

            pass = material.FindPass("ForwardLit");
            return pass >= 0 ? pass : -1;
        }

        // Zero the args so a stale instanceCount cannot leak into a frame with no tiles.
        readonly uint[] _zeroArgs = new uint[5];
        void ResetArgsToZero()
        {
            _argsHigh.SetData(_zeroArgs);
            _argsLow.SetData(_zeroArgs);
        }

        void ResetArgsToZero(UnsafeCommandBuffer cmd)
        {
            cmd.SetBufferData(_argsHigh, _zeroArgs);
            cmd.SetBufferData(_argsLow,  _zeroArgs);
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
