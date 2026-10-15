using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static IDisposable LockAndReconcileSavedAssetItems(ProjectWorkspace source, EditorSessionHistory? history)
    {
        var locks = new SavedAssetFileReconciliation();
        if (history == null) return locks;
        try
        {
            var proven = new List<Stride.Core.Assets.AssetItem>();
            foreach (var item in source.AssetItems.Where(item => item.IsDirty && !item.IsDeleted && !item.Package.IsReadOnly))
            {
                var state = history.Registry.Find(item.Id);
                if (state is not { IsPublished: true, IsDisposed: false } || state.AssetId != item.Id
                    || state.Document is not IAssetTagDocument tagged || tagged.AuthoredAsset.Id != item.Id
                    || state.Document is not IRelocatableEditorDocument owned || !owned.CanSave || owned.IsDirty
                    || owned.DiskContentHash.IsEmpty || !ReferenceEquals(history.Registry.Find(state.CanonicalPath), state)
                    || !string.Equals(Path.GetFullPath(item.FullPath.ToOSPath()), Path.GetFullPath(owned.Path), StringComparison.OrdinalIgnoreCase)) continue;
                var file = new FileStream(owned.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Files.Add(file);
                if (!SHA256.HashData(file).AsSpan().SequenceEqual(owned.DiskContentHash.Span))
                    throw new IOException("The saved asset changed externally: " + owned.Path);
                proven.Add(item);
            }
            // Asset filtering protects document-owned graphs from the package's
            // stale loaded objects. Only acknowledge actual persisted canonical
            // files; the native save still owns clearing collection/session flags.
            foreach (var item in proven) item.IsDirty = false;
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }

    private sealed class SavedAssetFileReconciliation : IDisposable
    {
        internal List<FileStream> Files { get; } = [];
        public void Dispose()
        {
            List<Exception>? errors = null;
            foreach (var file in Files)
                try { file.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            Files.Clear();
            if (errors != null) throw new AggregateException("Saved asset file lock cleanup failed.", errors);
        }
    }
}
