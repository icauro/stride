using System;
using Stride.Core.ReferenceCounting;

namespace Stride.Rendering
{
    public partial class EffectSystem
    {
        /// <summary>Releases cached native effects while retaining the actual compiler and shader watcher.</summary>
        public void ReleaseGraphicsResourcesForRecovery()
        {
            lock (cachedEffects)
            {
                isInitialized = false;
                earlyCompilerCache.Clear();
                // Remove only after a successful release, so a failed owner remains available.
                foreach (var bytecode in new System.Collections.Generic.List<Stride.Shaders.EffectBytecode>(cachedEffects.Keys))
                {
                    cachedEffects[bytecode].ReleaseInternal();
                    cachedEffects.Remove(bytecode);
                }
            }
        }

        /// <summary>Allows compiler results to create effects on the replacement graphics device.</summary>
        public void RebindGraphicsResourcesAfterRecovery()
        {
            lock (cachedEffects)
            {
                if (GraphicsDevice == null || GraphicsDevice.GraphicsDeviceStatus != Stride.Graphics.GraphicsDeviceStatus.Normal)
                    throw new InvalidOperationException("A usable replacement graphics device is required.");
                isInitialized = true;
            }
        }
    }
}
