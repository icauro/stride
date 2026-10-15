using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    /// <summary>Moves unchanged disk content and its retained identity as one recoverable operation.</summary>
    private void RelocateAssetDocumentPath(string previousPath, string nextPath)
    {
        previousPath = Path.GetFullPath(previousPath); nextPath = Path.GetFullPath(nextPath);
        if (string.Equals(previousPath, nextPath, StringComparison.Ordinal)) return;
        var caseOnly = string.Equals(previousPath, nextPath, StringComparison.OrdinalIgnoreCase);
        if (projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating || IsUserProjectBuilding)
            throw new InvalidOperationException("Wait for the current asset operation to finish.");
        var entry = documents.Find(previousPath);
        var owner = authoring?.Find(previousPath);
        if ((entry?.Document ?? owner?.Document) is not IRelocatableEditorDocument document)
            throw new InvalidOperationException("This asset document cannot be moved.");
        if (!caseOnly && (documents.Find(nextPath) != null || authoring?.Find(nextPath) != null || File.Exists(nextPath) || Directory.Exists(nextPath)))
            throw new IOException("An asset already exists at the destination path.");

        if (document.DiskContentHash.IsEmpty && (File.Exists(previousPath) || Directory.Exists(previousPath)))
            throw new IOException("The unsaved source path is occupied.");
        using var source = document.DiskContentHash.IsEmpty ? null : new WindowsOwnedAssetFileRename(previousPath, document.DiskContentHash.Span);
        var directory = Path.GetDirectoryName(nextPath)!;
        var created = new List<string>();
        for (var current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current)
            ?? throw new IOException("The asset destination has no existing parent directory.")) created.Add(current);
        var moved = false; var reindexed = false;
        try
        {
            Directory.CreateDirectory(directory);
            if (source != null) { source.Move(nextPath); moved = true; }
            if (entry != null) documents.RelocatePath(entry, nextPath);
            else
            {
                document.RelocatePath(nextPath);
                try { authoring!.Reindex(owner!); }
                catch { document.RelocatePath(previousPath); authoring!.Reindex(owner!); throw; }
            }
            reindexed = true;
            InvalidateCompiledTexture(previousPath);
        }
        catch (Exception failure)
        {
            try
            {
                if (reindexed)
                {
                    if (entry != null) documents.RelocatePath(entry, previousPath);
                    else { document.RelocatePath(previousPath); authoring!.Reindex(owner!); }
                }
                if (moved) source!.Move(previousPath);
                foreach (var path in created)
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            catch (Exception recovery)
            {
                throw new AggregateException("The asset move failed and could not be fully restored.", failure, recovery);
            }
            throw;
        }

        if (SamePath(selectedAssetPath, previousPath)) selectedAssetPath = nextPath;
        if (SamePath(assetPreviewPath, previousPath)) assetPreviewPath = nextPath;
        if (SamePath(updatingAssetPath, previousPath)) updatingAssetPath = nextPath;
        if (SamePath(pendingExternalAssetPath, previousPath)) pendingExternalAssetPath = nextPath;
        if (assetTagDrafts.Remove(previousPath, out var draft)) assetTagDrafts[nextPath] = draft;
    }
    private static bool SamePath(string? value, string expected) => value != null
        && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
