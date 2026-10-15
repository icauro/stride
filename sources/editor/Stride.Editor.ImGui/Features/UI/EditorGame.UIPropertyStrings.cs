namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUIString(UIDocument source, object identity, bool mixed, Func<bool> canEdit,
        Func<object?[]> read, Action<string> commit, Func<IDisposable>? acquireTarget)
    {
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        PropertyGrid.Strings.Draw(identity, mixed, Editable, read, commit, () =>
        {
            if (!Editable()) throw new InvalidOperationException("The text property owner is no longer current.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(sessionHistory!.Registry.Find(source.Asset.Id)!.AcquireView());
                if (acquireTarget != null) leases.Add(acquireTarget());
                return new PropertyStringOwners(leases, [source]);
            }
            catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
        });
    }
    private void DrawUIPropertyString(UIDocument source, Guid[] ids, string path, bool mixed)
    {
        var fields = ids.Select(id => source.Properties(id).Single(field => field.Name == path)).ToArray();
        if (fields.Length == 0) return;
        bool CanEdit() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source)
            && fields.Select((field, index) => source.Properties(ids[index]).Any(current => current.Name == path
                && ReferenceEquals(current.Node, field.Node) && source.CanEdit(current))).All(current => current);
        PropertyGrid.Strings.Draw(fields[0].Node, mixed, CanEdit,
            () => fields.Select(field => field.Node.Retrieve()).ToArray(),
            replacement => source.SetProperties(ids, path, replacement),
            () =>
            {
                if (!CanEdit()) throw new InvalidOperationException("The text property owner is no longer current.");
                var owner = sessionHistory!.Registry.Find(source.Asset.Id)!;
                return new PropertyStringOwners([owner.AcquireView()], [source]);
            });
    }
}
