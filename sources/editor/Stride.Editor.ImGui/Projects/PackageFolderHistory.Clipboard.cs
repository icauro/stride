using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageFolderHistory
{
    internal void AddFolders(Package package, IReadOnlyList<string> paths)
    {
        if (package.IsReadOnly || paths.Any(path => string.IsNullOrEmpty(path) || path.Split('/').Any(part => part is "" or "." or ".."
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))) throw new InvalidOperationException("The pasted folders can't be authored.");
        var before = package.ExplicitFolders.ToArray();
        var after = before.Concat(paths.Select(path => new UDirectory(path))).Distinct().ToArray();
        SetClipboardFolders(package, before, after);
    }
    internal void RemoveFolder(Package package, string path)
    {
        if (package.IsReadOnly || string.IsNullOrWhiteSpace(path) || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("The selected folder cannot be removed.");
        var before = package.ExplicitFolders.ToArray();
        var after = before.Where(folder => !folder.ToString().Equals(path, StringComparison.OrdinalIgnoreCase)
            && !folder.ToString().StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        SetClipboardFolders(package, before, after);
    }
    internal void MoveFolder(Package package, string beforePath, string afterPath)
    {
        if (package.IsReadOnly || string.IsNullOrWhiteSpace(beforePath) || string.IsNullOrWhiteSpace(afterPath))
            throw new InvalidOperationException("The selected folder cannot be moved.");
        var before = package.ExplicitFolders.ToArray();
        var after = before.Select(folder =>
        {
            var path = folder.ToString().TrimEnd('/');
            return path.Equals(beforePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(beforePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                ? new UDirectory(afterPath + path[beforePath.Length..]) : folder;
        }).ToArray();
        SetClipboardFolders(package, before, after);
    }
    private void SetClipboardFolders(Package package, UDirectory[] before, UDirectory[] after)
    {
        RequireCurrentPackage(package, authoringWorkspace);
        if (before.Select(folder => folder.ToString()).SequenceEqual(after.Select(folder => folder.ToString()), StringComparer.Ordinal)) return;
        var participant = sessionHistory!.Registry.GetOrCreate(package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(authoringWorkspace!, package, sessionHistory));
        var beforeRevision = participant.Document.Revision; var afterRevision = participant.Document.AllocateRevision();
        var operation = new FolderOperation(package, before, after, participant.Document, beforeRevision, afterRevision, authoringWorkspace);
        var wasDirty = package.IsDirty;
        FolderOperation.Apply(package, after); participant.Document.RestoreRevision(afterRevision);
        try { sessionHistory.PushOperation(operation, participant); }
        catch { FolderOperation.Apply(package, before); participant.Document.RestoreRevision(beforeRevision); package.IsDirty = wasDirty; throw; }
        Version++;
    }
}
