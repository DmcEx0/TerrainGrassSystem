using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace TerrainGrassSystem
{
    public sealed class GrassRenderPassFeature : ScriptableRendererFeature
    {
        [SerializeField] RenderPassEvent _renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        GrassRenderPass _pass;

        public override void Create()
        {
            _pass = new GrassRenderPass
            {
                renderPassEvent = _renderPassEvent,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (!IsSupportedCamera(camera)) return;
            if (!GrassTerrain.HasAnyRenderPassTerrain) return;

            _pass ??= new GrassRenderPass();
            _pass.renderPassEvent = _renderPassEvent;
            _pass.ConfigureInput(IsGameCamera(camera) && GrassTerrain.HasAnyDepthOcclusionRenderPassTerrain
                ? ScriptableRenderPassInput.Depth
                : ScriptableRenderPassInput.None);
            GrassTerrain.MarkRenderPassQueued();
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

        static bool IsSupportedCamera(Camera camera)
        {
            return camera != null
                && (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView);
        }

        sealed class GrassRenderPass : ScriptableRenderPass
        {
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
                if (!IsSupportedCamera(cameraData.camera)) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var colorTexture = resourceData.activeColorTexture;
                var depthTexture = resourceData.activeDepthTexture;
                if (!colorTexture.IsValid() || !depthTexture.IsValid()) return;

                bool enableDepthOcclusion = IsGameCamera(cameraData.camera)
                    && GrassTerrain.HasAnyDepthOcclusionRenderPassTerrain;
                var occlusionDepthTexture = enableDepthOcclusion
                    ? resourceData.cameraDepthTexture
                    : TextureHandle.nullHandle;
                using var builder = renderGraph.AddUnsafePass<PassData>("Terrain Grass Render Pass", out var passData);

                passData.ColorTexture = colorTexture;
                passData.DepthTexture = depthTexture;
                passData.OcclusionDepthTexture = occlusionDepthTexture;
                passData.Camera = cameraData.camera;
                passData.ViewMatrix = cameraData.GetViewMatrix();
                passData.ProjectionMatrix = cameraData.GetProjectionMatrix();

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
