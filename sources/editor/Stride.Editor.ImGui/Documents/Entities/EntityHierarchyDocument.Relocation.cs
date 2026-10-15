namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument : IRelocatableEditorDocument
{
    public ReadOnlyMemory<byte> DiskContentHash => diskHash;
    public void RelocatePath(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        DocumentAssetPath.Update(hierarchyItem, path);
        Path = path;
    }
}
