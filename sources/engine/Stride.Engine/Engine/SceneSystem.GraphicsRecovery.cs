using Stride.Core;
using Stride.Rendering;
using Stride.Graphics;

namespace Stride.Engine
{
    public partial class SceneSystem
    {
        /// <summary>Releases runtime scene/render owners; authored data belongs to the host and is untouched.</summary>
        public void ReleaseGraphicsResourcesForRecovery()
        {
            if (sceneTask != null && !sceneTask.IsCompleted || compositorTask != null && !compositorTask.IsCompleted)
                throw new System.InvalidOperationException("Scene content loads must drain before graphics recovery.");
            if (sceneTask != null)
            {
                if (sceneTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    Content.Unload(sceneTask.Result);
                sceneTask = null;
            }
            if (compositorTask != null)
            {
                if (compositorTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    Content.Unload(compositorTask.Result);
                compositorTask = null;
            }
            if (GraphicsCompositor != null)
            {
                GraphicsCompositor.Dispose();
                GraphicsCompositor = null;
            }
            if (SceneInstance != null)
            {
                ((IReferencable)SceneInstance).Release();
                SceneInstance = null;
            }
            if (renderDrawContext != null)
            {
                renderDrawContext.Dispose();
                renderDrawContext = null;
            }
            renderContext = null;
            if (splashScreenTexture != null)
            {
                Content.Unload(splashScreenTexture);
                splashScreenTexture = null;
            }
            splashScreenState = SplashScreenState.Invalid;
        }

        /// <summary>Creates an empty runtime scene and contexts; the host restores its canonical scene separately.</summary>
        public void RebindGraphicsResourcesAfterRecovery()
        {
            renderContext = RenderContext.GetShared(Services);
            renderDrawContext = new RenderDrawContext(Services, renderContext, Services.GetSafeServiceAs<GraphicsContext>());
            SceneInstance = new SceneInstance(Services) { RootScene = new Scene() };
        }
    }
}
