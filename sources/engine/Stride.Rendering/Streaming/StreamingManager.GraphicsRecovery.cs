namespace Stride.Streaming
{
    public partial class StreamingManager
    {
        private bool isGraphicsRecoveryPending;

        /// <summary>Cancels native-resource streaming without blocking the game thread.</summary>
        public bool TryReleaseGraphicsResourcesForRecovery()
        {
            lock (resources)
            {
                isGraphicsRecoveryPending = true;
                foreach (var resource in resources)
                    resource.RequestStopStreamingForRecovery();
                foreach (var resource in resources)
                    if (resource.IsTaskActive)
                        return false;
                // The completed staging payload is discarded, never uploaded onto a lost device.
                foreach (var resource in resources.ToArray())
                    resource.Release();
                resourcesLookup.Clear();
                activeStreaming.Clear();
                currentlyAllocatedMemory = 0;
                lastUpdateResourcesIndex = 0;
                return true;
            }
        }

        /// <summary>Enables registration of newly loaded textures on the replacement device.</summary>
        public void RebindGraphicsResourcesAfterRecovery() => isGraphicsRecoveryPending = false;
    }
}
