using System;
using Stride.Graphics;

namespace Stride.Games
{
    public partial class GraphicsDeviceManager
    {
        private bool graphicsRecoverySuspended;
        internal void SuspendDeviceForRecovery()
        {
            lock (lockDeviceCreation) { graphicsRecoverySuspended = true; beginDrawOk = false; }
        }
        internal void ResumeDeviceAfterRecovery()
        {
            lock (lockDeviceCreation)
            {
                graphicsRecoverySuspended = false;
                try { if (deviceSettingsChanged) ChangeOrCreateDevice(forceCreate: false); }
                catch { graphicsRecoverySuspended = true; throw; }
            }
        }
        // Only GameBase's explicit recovery boundary may call these. ApplyChanges retains
        // its original rejection of device replacement for hosts without resource rebinding.
        internal void ReleaseDeviceForRecovery()
        {
            lock (lockDeviceCreation)
            {
                beginDrawOk = false;
                var previous = GraphicsDevice;
                if (previous == null)
                    return;

                if (previous.Presenter != null)
                {
                    previous.Presenter.Dispose();
                    previous.Presenter = null;
                }
                previous.Dispose();
                previous.Disposing -= GraphicsDevice_Disposing;
                GraphicsDevice = null;
            }
        }

        internal void CreateDeviceAfterRecovery()
        {
            lock (lockDeviceCreation)
            {
                if (GraphicsDevice != null)
                    throw new InvalidOperationException("The previous graphics device has not been released.");
                ChangeOrCreateDevice(forceCreate: true);
            }
        }
    }
}
