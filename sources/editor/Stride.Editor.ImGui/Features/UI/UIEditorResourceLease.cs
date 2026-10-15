namespace Stride.Editor.ImGui;

internal sealed record UIEditorResourceLease(object Value, IDisposable Owner) : IDisposable
{
    public void Dispose() => Owner.Dispose();
}
