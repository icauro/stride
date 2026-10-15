using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record CompositorDraftTarget(CompositorProperty[] Fields, Func<bool> Editable,
        Func<object?[]> Read, Func<IDisposable> Acquire, Action<Func<CompositorProperty, object?>> Apply);
    private CompositorDraftTarget CaptureCompositorDraftTarget(AssetMetadataDocument source,
        IReadOnlyList<CompositorProperty> properties, CompositorAggregateProperty? aggregate = null,
        Func<CompositorProperty, bool>? canEditField = null)
    {
        var fields = properties.ToArray(); var providerVersion = CompositorTypeCatalog.Version;
        var extensionVersion = source.SessionHistory?.Extensions?.Version;
        bool Editable() => CanStartAssetEdit && providerVersion == CompositorTypeCatalog.Version
            && sessionHistory?.Registry.Find(source.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } state
            && ReferenceEquals(state.Document, source) && source.SessionHistory?.Extensions?.Version == extensionVersion
            && fields.All(field => (canEditField?.Invoke(field) ?? source.CanEditCompositorProperty(field)) && !source.CompositorPresenterReadOnly(field));
        object?[] Read() => fields.Select(source.CompositorPropertyValue).Concat(fields.SelectMany(field => new object?[]
        {
            field.Accessor.Node, field.Accessor.Index, field.ItemId,
            field.ParentCollection?.Accessor.RetrieveValue(), field.PresentationOwner?.Accessor.RetrieveValue(),
            field.Accessor.Node is IMemberNode member ? member.Parent.Retrieve() : null,
        })).ToArray();
        IDisposable Acquire()
        {
            if (!Editable()) throw new InvalidOperationException("The compositor property owner is no longer current.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(sessionHistory!.Registry.Find(source.AuthoredAsset.Id)!.AcquireView());
                foreach (var field in fields)
                    if (source.AcquireCompositorPropertyPresenter(field) is { } target) leases.Add(target);
                return new PropertyStringOwners(leases, [source]);
            }
            catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
        }
        void Apply(Func<CompositorProperty, object?> replacement)
        {
            UseTextureSettingsCommandContext();
            if (aggregate != null) source.EditCompositorAggregate(aggregate, replacement);
            else { source.UpdateCompositorProperty(fields[0], replacement(fields[0])); source.EndCompositorSettingEdit(); }
        }
        return new(fields, Editable, Read, Acquire, Apply);
    }
}
