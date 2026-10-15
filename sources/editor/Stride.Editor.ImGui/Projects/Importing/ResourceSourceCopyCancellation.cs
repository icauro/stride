namespace Stride.Editor.ImGui;

internal sealed class ResourceSourceCopyCancellation(ResourceSourceCopyOperation operation) : IDisposable
{
    private bool completed;
    public void Dispose() { if (!completed) { operation.CancelInitial(); completed = true; } }
}
