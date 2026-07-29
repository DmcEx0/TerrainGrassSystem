using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace TerrainGrassSystem
{
    public sealed class GrassRenderPassFeature : ScriptableRendererFeature
    {
        [SerializeField] RenderPassEvent _renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        [Tooltip("Draw the latest Game camera grass buffers in Scene View instead of regenerating grass for the Scene camera.")]
        [SerializeField] bool _showGameCameraResultInSceneView = true;

        GrassRenderPass _pass;

        public override void Create()
        {
            _pass = new GrassRenderPass
            {
                renderPassEvent = _renderPassEvent,
                ShowGameCameraResultInSceneView = _showGameCameraResultInSceneView,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (!IsSupportedCamera(camera)) return;
            bool gameCamera = IsGameCamera(camera);
            if (gameCamera
                    ? !GrassTerrain.HasAnyRuntimeTerrainForCamera(camera)
                    : !GrassTerrain.HasAnyRuntimeTerrain)
                return;

            _pass ??= new GrassRenderPass();
            _pass.renderPassEvent = _renderPassEvent;
            _pass.ShowGameCameraResultInSceneView = _showGameCameraResultInSceneView;
            _pass.ConfigureInput(gameCamera && GrassTerrain.HasAnyDepthOcclusionRuntimeTerrainForCamera(camera)
                ? ScriptableRenderPassInput.Depth
                : ScriptableRenderPassInput.None);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            _pass = null;
        }

        static bool IsGameCamera(Camera camera)
        {
            return camera != null && camera.cameraType == CameraType.Game;
        }

        static bool IsSceneCamera(Camera camera)
        {
            return camera != null && camera.cameraType == CameraType.SceneView;
        }

        static bool IsSupportedCamera(Camera camera)
        {
            return camera != null
                && (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView);
        }

        sealed class GrassRenderPass : ScriptableRenderPass
        {
            static readonly int s_DrawObjectPassDataId   = Shader.PropertyToID("_DrawObjectPassData");
            static readonly int s_AlphaToMaskAvailableId = Shader.PropertyToID("_AlphaToMaskAvailable");

            internal bool ShowGameCameraResultInSceneView;

            sealed class GeneratePassData
            {
                public TextureHandle OcclusionDepthTexture;
                public Camera Camera;
                public Matrix4x4 ViewMatrix;
                public Matrix4x4 ProjectionMatrix;
                public Vector4 DepthTexelSize;
            }

            sealed class DrawPassData
            {
                public int MsaaSamples;
                public Camera Camera;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                if (!IsSupportedCamera(cameraData.camera)) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var colorTexture = resourceData.activeColorTexture;
                var depthTexture = resourceData.activeDepthTexture;
                if (!colorTexture.IsValid() || !depthTexture.IsValid()) return;

                bool sceneViewDrawsGameResult = IsSceneCamera(cameraData.camera) && ShowGameCameraResultInSceneView;
                bool enableDepthOcclusion = !sceneViewDrawsGameResult
                    && IsGameCamera(cameraData.camera)
                    && GrassTerrain.HasAnyDepthOcclusionRuntimeTerrainForCamera(cameraData.camera);
                var occlusionDepthTexture = enableDepthOcclusion
                    ? resourceData.cameraDepthTexture
                    : TextureHandle.nullHandle;

                if (!sceneViewDrawsGameResult)
                {
                    using var generateBuilder = renderGraph.AddUnsafePass<GeneratePassData>(
                        "Terrain Grass Generate Pass", out var generateData);

                    generateData.OcclusionDepthTexture = occlusionDepthTexture;
                    generateData.Camera = cameraData.camera;
                    generateData.ViewMatrix = cameraData.GetViewMatrix();
                    generateData.ProjectionMatrix = cameraData.GetProjectionMatrix();

                    var depthInfo = renderGraph.GetRenderTargetInfo(
                        occlusionDepthTexture.IsValid() ? occlusionDepthTexture : depthTexture);
                    int width = Mathf.Max(1, depthInfo.width);
                    int height = Mathf.Max(1, depthInfo.height);
                    generateData.DepthTexelSize = new Vector4(1f / width, 1f / height, width, height);

                    generateBuilder.UseTexture(depthTexture, AccessFlags.Read);
                    if (occlusionDepthTexture.IsValid())
                        generateBuilder.UseTexture(occlusionDepthTexture, AccessFlags.Read);
                    generateBuilder.AllowPassCulling(false);
                    generateBuilder.AllowGlobalStateModification(true);
                    generateBuilder.SetRenderFunc((GeneratePassData data, UnsafeGraphContext context) =>
                    {
                        var terrains = GrassTerrain.ActiveTerrains;
                        for (int i = 0; i < terrains.Count; ++i)
                        {
                            terrains[i]?.GenerateFromRenderPass(
                                context.cmd,
                                data.Camera,
                                data.OcclusionDepthTexture,
                                data.ViewMatrix,
                                data.ProjectionMatrix,
                                data.DepthTexelSize);
                        }
                    });
                }

                using var drawBuilder = renderGraph.AddRasterRenderPass<DrawPassData>(
                    "Terrain Grass Draw Pass", out var drawData);

                drawData.MsaaSamples = cameraData.cameraTargetDescriptor.msaaSamples;
                drawData.Camera = cameraData.camera;

                drawBuilder.SetRenderAttachment(colorTexture, 0, AccessFlags.Write);
                drawBuilder.SetRenderAttachmentDepth(depthTexture, AccessFlags.ReadWrite);
                drawBuilder.UseAllGlobalTextures(true);
                drawBuilder.AllowPassCulling(false);
                drawBuilder.AllowGlobalStateModification(true);
                drawBuilder.SetRenderFunc((DrawPassData data, RasterGraphContext context) =>
                {
                    SetupOpaqueDrawGlobals(context.cmd, data.MsaaSamples);
                    GrassInteractionManager.PushShaderGlobals(context.cmd);

                    var terrains = GrassTerrain.ActiveTerrains;
                    for (int i = 0; i < terrains.Count; ++i)
                        terrains[i]?.DrawLastRenderPassResult(context.cmd, data.Camera);
                });
            }

            static void SetupOpaqueDrawGlobals(RasterCommandBuffer cmd, int msaaSamples)
            {
                cmd.SetGlobalVector(s_DrawObjectPassDataId, new Vector4(0f, 0f, 0f, 1f));
                cmd.SetGlobalFloat(s_AlphaToMaskAvailableId, msaaSamples > 1 ? 1f : 0f);
            }
        }
    }
}
