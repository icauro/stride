using Hexa.NET.ImGui;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentPath(ComponentField field)
    {
        var owner = document!;
        Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;

            try
            {
                DrawTypedPropertyPath(owner, field.Type, field.Node.Retrieve(),
                    () => ReferenceEquals(document, owner) && owner.CanEditComponentValue(field) && field.SupportsEditing,
                    () => field.Node.Retrieve(), path => { owner.UpdateComponent(field, path); owner.EndComponentEdit(); }, identity: field.Node);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }

    private void DrawGameSettingPath(AssetMetadataDocument owner, GameSettingField field, object? value)
        => DrawTypedPropertyPath(owner, field.Type, value, () => owner.CanEditPresentedGameSetting(field),
            () => field.Node.Retrieve(), path => { owner.UpdateGameSetting(field, path); owner.EndGameSettingEdit(); });

    private void DrawTypedPropertyPath(IEditorDocument owner, Type type, object? value, Func<bool> canEdit,
        Func<object?> read, Action<UPath> set, bool mixed = false, object? identity = null,
        Func<object?[]>? readValues = null, Func<IDisposable>? acquire = null, int valueCount = 1)
        => DrawRetainedPath(owner, identity ?? owner, type, canEdit, readValues ?? (() => [read()]), set, valueCount,
            acquire ?? (() => owner is EntityHierarchyDocument hierarchy ? AcquirePropertyStringOwners([hierarchy])
                : owner is IAssetTagDocument asset ? AcquirePropertyStringOwner(asset)
                : throw new InvalidOperationException("The path editor has no canonical authored owner.")));
}