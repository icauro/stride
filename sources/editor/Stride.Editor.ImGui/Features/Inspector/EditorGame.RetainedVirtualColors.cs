using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IDisposable AcquireRetainedVirtualColor(IEditorDocument owner, Func<IDisposable> acquireTarget)
    {
        var resources = new List<IDisposable>();
        try
        {
            resources.Add(owner is EntityHierarchyDocument hierarchy ? AcquirePropertyStringOwners([hierarchy])
                : owner is IAssetTagDocument asset ? AcquirePropertyStringOwner(asset)
                : throw new InvalidOperationException("The color property has no canonical owner."));
            resources.Add(acquireTarget());
            return new PropertyStringOwners(resources, [owner]);
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(resources, [owner]);
            try { cleanup.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
    private IDisposable AcquireRetainedVirtualColor(IEditorDocument owner, VirtualNodePresenter presenter)
        => AcquireRetainedVirtualColor(owner, () => owner switch
        {
            ProceduralAssetDocument procedural => procedural.AcquirePresenterTarget(presenter),
            AssetMetadataDocument asset => asset.AcquireExtensionVirtualTarget(presenter),
            _ => throw new InvalidOperationException("The native color presenter owner is unavailable.")
        });
    private void DrawRetainedCombinedVirtualColor(IReadOnlyList<ComponentVirtualOwner> source)
    {
        var entries = source.ToArray(); var first = entries[0].Node;
        bool Editable() => !IsAuthoringPipelineUpdating && entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node));
        DrawRetainedColor(first, Nullable.GetUnderlyingType(first.Type) ?? first.Type,
            () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(), Editable,
            transform => ApplyCombinedComponentVirtualCore(entries, (_, value) => transform(value)), () => AcquireRetainedCombinedVirtualTargets(entries),
            axis => entries.All(entry => !NativeNumericAxes.IsReadOnly(entry.Node, axis)), valueCount: entries.Length);
    }
    private IDisposable AcquireRetainedCombinedVirtualTargets(IReadOnlyList<ComponentVirtualOwner> entries)
    {
        var resources = new List<IDisposable>();
        try
        {
            resources.Add(AcquirePropertyStringOwners(entries.Select(entry => entry.Document)));
            foreach (var entry in entries) resources.Add(entry.Presentation.AcquireVirtualTarget(entry.Node));
            return new PropertyStringOwners(resources, entries.Select(entry => (IEditorDocument)entry.Document).Distinct().ToArray());
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(resources, entries.Select(entry => (IEditorDocument)entry.Document).Distinct().ToArray());
            try { cleanup.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
}
