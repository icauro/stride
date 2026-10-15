namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession
{
    internal EditorComponentImageLease? AcquireComponentImage(Type componentType)
    {
        Check(); if (stopped) return null;
        var owner = providers.LastOrDefault(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type)
            && (provider.Available || provider.Type.Assembly == componentType.Assembly)
            && provider.Registration.ComponentImages.ContainsKey(componentType));
        if (owner == null) return null;
        owner.Leases++;
        var released = false;
        void Release()
        {
            Check(); if (released) return;
            owner.Leases--; released = true;
        }
        return new(owner.Registration.ComponentImages[componentType], Release);
    }
}

internal sealed class EditorComponentImageLease(byte[] bytes, Action release) : IDisposable
{
    internal ReadOnlyMemory<byte> Bytes { get; } = bytes;
    private bool disposed;
    public void Dispose() { if (disposed) return; release(); disposed = true; }
}
