namespace Stride.Games
{
    /// <summary>Device-bound game systems retained across explicit host graphics recovery.</summary>
    public interface IGraphicsDeviceRecoveryParticipant
    {
        /// <summary>Cancel/drain device work and release native owners. Return false while still draining.</summary>
        bool TryReleaseGraphicsResourcesForRecovery();
        /// <summary>Recreate resources from actual CPU state against the new device/context services.</summary>
        void RebindGraphicsResourcesAfterRecovery();
    }
}
