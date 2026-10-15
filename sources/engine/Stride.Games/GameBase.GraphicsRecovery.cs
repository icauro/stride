using System;
using Stride.Graphics;

namespace Stride.Games
{
    public abstract partial class GameBase
    {
        private GraphicsRecoveryPhase graphicsRecoveryPhase;
        private Exception graphicsRecoveryNativeFailure;
        private Action graphicsRecoveryRelease;
        private Action graphicsRecoveryRebuild;

        /// <summary>Whether frames are suspended for an explicit graphics recovery.</summary>
        protected bool IsGraphicsDeviceRecoveryPending => graphicsRecoveryPhase != GraphicsRecoveryPhase.None;

        /// <summary>The actual terminal native replacement failure; authored hosts may still save or exit without rendering.</summary>
        protected Exception GraphicsDeviceRecoveryFailure => graphicsRecoveryNativeFailure;

        /// <summary>
        /// Allows an explicitly recovery-capable host to schedule teardown at the next
        /// frame boundary. Returning false retains the default exception/restart policy.
        /// </summary>
        protected virtual bool OnGraphicsDeviceLost(GraphicsDeviceException error) => false;

        protected void SuspendGraphicsDeviceRecovery(Action releaseOwners, Action rebuildOwners)
        {
            if (releaseOwners == null) throw new ArgumentNullException(nameof(releaseOwners));
            if (rebuildOwners == null) throw new ArgumentNullException(nameof(rebuildOwners));
            if (graphicsRecoveryPhase != GraphicsRecoveryPhase.None) return;
            graphicsRecoveryRelease = releaseOwners;
            graphicsRecoveryRebuild = rebuildOwners;
            graphicsRecoveryPhase = GraphicsRecoveryPhase.ReleaseOwners;
            (graphicsDeviceManager as GraphicsDeviceManager)?.SuspendDeviceForRecovery();
        }

        /// <summary>
        /// Replaces the real device and command context after the host has released every
        /// device-bound owner. Call on the game thread outside an active draw. Neither
        /// callback may replace authored documents or rerun game initialization.
        /// </summary>
        /// <remarks>
        /// Successful phases are retained across a failed rebind. A native teardown or
        /// creation failure is terminal for this boundary; it must not be mistaken for a
        /// recoverable partially disposed native device. Hosts should surface that failure
        /// while preserving their authored session for recovery or explicit restart.
        /// </remarks>
        protected void RecoverGraphicsDevice(Action releaseOwners, Action rebuildOwners)
        {
            if (releaseOwners == null) throw new ArgumentNullException(nameof(releaseOwners));
            if (rebuildOwners == null) throw new ArgumentNullException(nameof(rebuildOwners));
            if (beginDrawOk && GraphicsDevice != null && GraphicsDevice.GraphicsDeviceStatus == GraphicsDeviceStatus.Normal)
                throw new InvalidOperationException("Graphics recovery cannot run inside a draw.");
            if (graphicsDeviceManager is not GraphicsDeviceManager manager)
                throw new NotSupportedException("This graphics manager does not support explicit device recovery.");
            if (graphicsRecoveryNativeFailure != null)
                throw new InvalidOperationException("Native graphics recovery failed; the game must be restarted.", graphicsRecoveryNativeFailure);

            // Lost-device frames deliberately skip EndDraw: abandon that dead command list,
            // rather than trying to flush it merely to clear the frame marker.
            beginDrawOk = false;

            if (graphicsRecoveryPhase == GraphicsRecoveryPhase.None)
            {
                SuspendGraphicsDeviceRecovery(releaseOwners, rebuildOwners);
            }

            if (graphicsRecoveryPhase == GraphicsRecoveryPhase.ReleaseOwners)
            {
                // Host release must be idempotent: a failed owner keeps its actual resources
                // and must be retried rather than letting a new device escape the boundary.
                graphicsRecoveryRelease();
                Services.RemoveService<GraphicsContext>();
                GraphicsContext = null;
                graphicsRecoveryPhase = GraphicsRecoveryPhase.ReplaceDevice;
            }

            if (graphicsRecoveryPhase == GraphicsRecoveryPhase.ReplaceDevice)
            {
                try
                {
                    manager.ReleaseDeviceForRecovery();
                    GraphicsDevice = null;
                    manager.CreateDeviceAfterRecovery();
                    GraphicsDevice = manager.GraphicsDevice;
                    GraphicsContext = new GraphicsContext(GraphicsDevice);
                    Services.AddService(GraphicsContext);
                    graphicsRecoveryPhase = GraphicsRecoveryPhase.RebuildOwners;
                }
                catch (Exception error)
                {
                    graphicsRecoveryNativeFailure = error;
                    throw;
                }
            }

            if (graphicsRecoveryPhase == GraphicsRecoveryPhase.RebuildOwners)
            {
                graphicsRecoveryRebuild();
                if (GraphicsDevice.GraphicsDeviceStatus != GraphicsDeviceStatus.Normal)
                {
                    graphicsRecoveryNativeFailure = GraphicsDeviceException.FromLostDevice(GraphicsDevice, GraphicsDevice.GraphicsDeviceStatus);
                    throw graphicsRecoveryNativeFailure;
                }
                manager.ResumeDeviceAfterRecovery();
                graphicsRecoveryRelease = null;
                graphicsRecoveryRebuild = null;
                graphicsRecoveryPhase = GraphicsRecoveryPhase.None;
                ResetElapsedTime();
            }
        }

        private enum GraphicsRecoveryPhase { None, ReleaseOwners, ReplaceDevice, RebuildOwners }
    }
}
