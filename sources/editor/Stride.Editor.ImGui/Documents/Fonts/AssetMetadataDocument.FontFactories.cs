using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry> FontSettingEntries(FontSettingField field)
        => IsCurrentFontSettingField(field)
            ? NativeAbstractEntries.Available(ExtensionAbstractEntries(field.Node), field.Types, field.AllowsNull) : [];

    internal void ChangeFontSettingEntry(FontSettingField field, AbstractNodeEntry entry)
    {
        CommitEdits();
        if (!CanEditFontSetting(field) || !NativeAbstractEntries.Contains(FontSettingEntries(field), entry))
            throw new InvalidOperationException("The native font entry is no longer available.");
        var current = field.Node.Retrieve();
        if (entry.IsMatchingValue(current!)) return;
        var value = entry.GenerateValue(current!);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateFontSetting(field, value);
        EndFontSettingEdit();
    }
}
