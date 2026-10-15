namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal bool HasPendingCompilationEdits => HasActiveChange;
}
