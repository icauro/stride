using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed partial class ImportContentOwner
    {
        private bool deviceResourcesReleased;
        public void ReleaseGraphicsResourcesForRecovery()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ImportContentOwner));
            if (Volatile.Read(ref PreviewLeases) != 0 || Preview is { IsDisposed: false })
                throw new InvalidOperationException("Imported content still has an active preview consumer.");
            if (deviceResourcesReleased) return;
            foreach (var value in Values)
                if (!unloaded.Contains(value.Value)) { Cache.Unload(value.Value); unloaded.Add(value.Value); }
            deviceResourcesReleased = true;
        }
        public void RebindGraphicsResourcesAfterRecovery()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ImportContentOwner));
            if (!deviceResourcesReleased) return;
            var replacement = Bundle.CreateObjectCache();
            var values = new List<EngineViewport.ImportedContent>();
            try
            {
                foreach (var value in Values)
                    values.Add(new(value.Type, value.Url, replacement.Load(value.Type, value.Url, ContentManagerLoaderSettings.StreamingDisabled)));
            }
            catch
            {
                foreach (var value in values) replacement.Unload(value.Value);
                throw;
            }
            Cache = replacement; Values = values.ToArray(); Preview = null;
            unloaded.Clear(); deviceResourcesReleased = false;
        }
    }
}
