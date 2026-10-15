namespace Stride.Audio
{
    public partial class AudioListener
    {
        private AudioEngine deviceEngine;
        internal void UpdateDeviceListener(AudioLayer.Listener value)
            => AudioLayer.ListenerPush3D(value, ref Position, ref forward, ref up, ref Velocity, ref WorldTransform);
    }
}
