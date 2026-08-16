using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Draws registered avatar submeshes once more with the lightweight
    /// inverted-hull material. The pass reuses each live renderer, so skinned
    /// meshes and morphs stay in sync without duplicate renderers or copies.
    /// </summary>
    public sealed class AvatarOutlineRendererFeature : ScriptableRendererFeature
    {
        private sealed class AvatarOutlinePass : ScriptableRenderPass
        {
            private readonly ProfilingSampler sampler = new ProfilingSampler("Banxia Avatar Outline");

            internal AvatarOutlinePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            }

            public override void Execute(
                ScriptableRenderContext context,
                ref RenderingData renderingData)
            {
                if (renderingData.cameraData.cameraType != CameraType.Game)
                {
                    return;
                }

                var commandBuffer = CommandBufferPool.Get("Banxia Avatar Outline");
                using (new ProfilingScope(commandBuffer, sampler))
                {
                    AvatarOutlineController.DrawRegistered(commandBuffer);
                }
                context.ExecuteCommandBuffer(commandBuffer);
                CommandBufferPool.Release(commandBuffer);
            }
        }

        private AvatarOutlinePass pass;

        public override void Create()
        {
            pass = new AvatarOutlinePass();
        }

        public override void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            if (pass != null)
            {
                renderer.EnqueuePass(pass);
            }
        }
    }
}
