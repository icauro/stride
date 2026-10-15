namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUIColor(UIDocument source, object identity, Type type, Func<bool> canEdit,
        Func<object?[]> read, Action<Func<object?, object?>> transform, int valueCount,
        Func<int, bool>? canEditAxis, Func<IDisposable>? acquireTarget)
    {
        var providerVersion = source.SessionHistory?.Extensions?.Version;
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && providerVersion == source.SessionHistory?.Extensions?.Version
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        object?[] Read() => read().Concat(new object?[] { source.SessionHistory?.Extensions?.Version }).ToArray();
        DrawRetainedColor(identity, type, Read, Editable, transform,
            () => AcquireUIValueDraftOwners(source, Editable, acquireTarget), canEditAxis, valueCount: valueCount);
    }
}
