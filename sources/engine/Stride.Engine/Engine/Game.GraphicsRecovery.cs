using System;
using System.Linq;
using Stride.Games;

namespace Stride.Engine
{
    public partial class Game
    {
        private int graphicsRecoveryReleasePosition;
        private int graphicsRecoveryRebindPosition;
        private bool engineGraphicsReleased;
        private IGraphicsDeviceRecoveryParticipant[] graphicsRecoveryParticipants;
        private int graphicsParticipantReleasePosition, graphicsParticipantRebindPosition;

        /// <summary>
        /// Drains streaming without waiting and releases actual GPU-facing engine owners.
        /// The host must release its viewports/content users first and retry when false.
        /// CPU input, audio, script systems and authored data remain installed.
        /// </summary>
        protected bool TryReleaseEngineGraphicsResourcesForRecovery()
        {
            if (engineGraphicsReleased) return true;
            if (graphicsRecoveryReleasePosition == 0)
            {
                if (!Streaming.TryReleaseGraphicsResourcesForRecovery()) return false;
                graphicsRecoveryReleasePosition = 1;
            }
            if (graphicsRecoveryReleasePosition == 1)
            {
                VRDeviceSystem.ReleaseGraphicsResourcesForRecovery();
                graphicsRecoveryReleasePosition = 2;
            }
            if (graphicsRecoveryReleasePosition == 2)
            {
                SceneSystem.ReleaseGraphicsResourcesForRecovery();
                graphicsRecoveryReleasePosition = 3;
            }
            if (graphicsRecoveryReleasePosition == 3)
            {
                DebugTextSystem.ReleaseGraphicsResourcesForRecovery();
                graphicsRecoveryReleasePosition = 4;
            }
            if (graphicsRecoveryReleasePosition == 4)
            {
                ProfilingSystem.ReleaseGraphicsResourcesForRecovery();
                graphicsRecoveryReleasePosition = 5;
            }
            if (graphicsRecoveryReleasePosition == 5)
            {
                gameFontSystem.ReleaseGraphicsResourcesForRecovery();
                graphicsRecoveryReleasePosition = 6;
            }
            if (graphicsRecoveryReleasePosition == 6)
            {
                EffectSystem.ReleaseGraphicsResourcesForRecovery();
                graphicsRecoveryReleasePosition = 7;
            }
            graphicsRecoveryParticipants ??= GameSystems.OfType<IGraphicsDeviceRecoveryParticipant>().ToArray();
            while (graphicsParticipantReleasePosition < graphicsRecoveryParticipants.Length)
            {
                if (!graphicsRecoveryParticipants[graphicsParticipantReleasePosition].TryReleaseGraphicsResourcesForRecovery()) return false;
                graphicsParticipantReleasePosition++;
            }
            engineGraphicsReleased = true;
            return true;
        }

        /// <summary>Recreates actual engine draw contexts/caches against the replacement device.</summary>
        protected void RebindEngineGraphicsResourcesAfterRecovery()
        {
            if (!engineGraphicsReleased) return;
            if (graphicsRecoveryRebindPosition == 0)
            {
                EffectSystem.RebindGraphicsResourcesAfterRecovery();
                Streaming.RebindGraphicsResourcesAfterRecovery();
                graphicsRecoveryRebindPosition = 1;
            }
            if (graphicsRecoveryRebindPosition == 1)
            {
                gameFontSystem.RebindGraphicsResourcesAfterRecovery();
                graphicsRecoveryRebindPosition = 2;
            }
            if (graphicsRecoveryRebindPosition == 2)
            {
                while (graphicsParticipantRebindPosition < graphicsRecoveryParticipants.Length)
                {
                    graphicsRecoveryParticipants[graphicsParticipantRebindPosition].RebindGraphicsResourcesAfterRecovery();
                    graphicsParticipantRebindPosition++;
                }
                SceneSystem.RebindGraphicsResourcesAfterRecovery();
                graphicsRecoveryRebindPosition = 3;
            }
            if (graphicsRecoveryRebindPosition == 3)
            {
                VRDeviceSystem.RebindGraphicsResourcesAfterRecovery();
                graphicsRecoveryRebindPosition = 4;
            }
            engineGraphicsReleased = false;
            graphicsRecoveryReleasePosition = graphicsRecoveryRebindPosition = 0;
            graphicsRecoveryParticipants = null;
            graphicsParticipantReleasePosition = graphicsParticipantRebindPosition = 0;
        }
    }
}
