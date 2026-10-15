namespace Stride.Editor.ImGui;

internal interface IEditorNativeMutationObserver : IDisposable
{
    void Complete();
}

internal partial class AssetMetadataDocument
{
    internal virtual IEditorNativeMutationObserver? BeginNativeAuthoringMutation() => null;
}
