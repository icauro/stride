namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUICharacter(UIDocument source, object identity, Func<bool> canEdit,
        Func<object?[]> read, Action<char> commit, int valueCount, Func<IDisposable>? acquireTarget)
    {
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        DrawRetainedCharacter(identity, read, Editable, commit,
            () => AcquireUIValueDraftOwners(source, Editable, acquireTarget), valueCount);
    }
}
