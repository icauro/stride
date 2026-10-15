using Stride.Games;

namespace Stride.Video
{
    public partial class VideoSystem
    {
        private bool graphicsRecoveryReleased;
        public bool TryReleaseGraphicsResourcesForRecovery()
        {
            if (graphicsRecoveryReleased) return true;
            ActiveBackendFactory?.DestroySystem(this);
            ActiveBackendFactory = null;
            graphicsRecoveryReleased = true;
            return true;
        }
        public void RebindGraphicsResourcesAfterRecovery()
        {
            if (!graphicsRecoveryReleased) return;
            var factory = VideoBackendRegistry.SelectFactory(Services.GetService<IGame>()?.GraphicsDevice);
            factory?.InitializeSystem(this);
            ActiveBackendFactory = factory;
            graphicsRecoveryReleased = false;
        }
    }
}
