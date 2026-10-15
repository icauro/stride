using Stride.Core.Serialization.Contents;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class RetiredModelContent(EditorGame host, CompiledProjectContent bundle, ContentManager cache, Model model) : IDisposable
    {
        private bool modelReleased, bundleReleased;
        public void Dispose()
        {
            if (!modelReleased && (host.MaterialPreviewUsesContent(bundle) || host.SceneViewsUseImportedContent([model])))
                throw new PreviewResourceDrainPendingException();
            if (!modelReleased) { cache.Unload(model); modelReleased = true; }
            if (!bundleReleased) { bundle.Dispose(); bundleReleased = true; }
        }
    }
    private readonly Dictionary<CompiledProjectContent, RetiredModelContent> modelCleanupOwners = new(ReferenceEqualityComparer.Instance);
    private RetiredModelContent GetModelCleanupOwner(CompiledProjectContent bundle, ContentManager cache, Model model)
    {
        if (!modelCleanupOwners.TryGetValue(bundle, out var owner))
            modelCleanupOwners.Add(bundle, owner = new RetiredModelContent(this, bundle, cache, model));
        return owner;
    }


    private readonly List<(CompiledProjectContent Bundle, ContentManager Cache, Model Model, string Url)> refreshedModels = [];

    private readonly List<(CompiledProjectContent Bundle, ContentManager Cache, Model Model, int Frame)> retiredModels = [];

    internal int RefreshedModelOwners => refreshedModels.Count;

    internal int RetiredModelOwners => retiredModels.Count;

    private void PumpRetiredRefreshedModels()
    {
        if (retiredModels.Count == 0) return;
        var sceneBorrowers = SnapshotSceneResourceViews(includeRetiring: true);
        foreach (var resource in retiredModels.Where(resource => frames >= resource.Frame && !MaterialPreviewUsesContent(resource.Bundle)
            && !sceneBorrowers.Any(view => view.Viewport.UsesImportedContent([resource.Model], view.Document))).ToArray())
        {
            RetireEditorPreview(GetModelCleanupOwner(resource.Bundle, resource.Cache, resource.Model));
            modelCleanupOwners.Remove(resource.Bundle); retiredModels.Remove(resource);
        }
    }

    private void ClearRefreshedModels()
    {
        foreach (var resource in refreshedModels.AsEnumerable().Reverse().ToArray())
        {
            GetModelCleanupOwner(resource.Bundle, resource.Cache, resource.Model).Dispose();
            modelCleanupOwners.Remove(resource.Bundle); refreshedModels.Remove(resource);
        }
        foreach (var resource in retiredModels.ToArray())
        {
            GetModelCleanupOwner(resource.Bundle, resource.Cache, resource.Model).Dispose();
            modelCleanupOwners.Remove(resource.Bundle); retiredModels.Remove(resource);
        }
    }
}
