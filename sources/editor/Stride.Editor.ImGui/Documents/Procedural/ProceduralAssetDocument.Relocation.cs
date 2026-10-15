namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument : IRelocatableEditorDocument
{
    public ReadOnlyMemory<byte> DiskContentHash => diskHash;
    public void RelocatePath(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        DocumentAssetPath.Update(item, path);
        if (graph != null) DocumentAssetPath.Update(graph.AuthoredItem, path);
        Path = path;
    }
}
