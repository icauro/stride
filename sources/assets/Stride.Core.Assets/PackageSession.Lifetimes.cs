namespace Stride.Core.Assets;

public sealed partial class PackageSession
{
    private readonly List<IDisposable> retainedLifetimes = [];

    /// <summary>Retains native package backing resources until session assemblies and artifacts have been released.</summary>
    public void RetainLifetime(IDisposable lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        lock (disposalGate)
        {
            if (disposalStarted) throw new ObjectDisposedException(nameof(PackageSession));
            if (!retainedLifetimes.Any(current => ReferenceEquals(current, lifetime))) retainedLifetimes.Add(lifetime);
        }
    }

    private void ReleaseRetainedLifetimes()
    {
        while (retainedLifetimes.Count != 0)
        {
            retainedLifetimes[^1].Dispose();
            retainedLifetimes.RemoveAt(retainedLifetimes.Count - 1);
        }
    }
}
