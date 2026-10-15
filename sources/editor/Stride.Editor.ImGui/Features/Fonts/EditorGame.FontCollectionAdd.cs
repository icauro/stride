using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawFontCollectionAdd(AssetMetadataDocument owner, FontSettingField field, Action<Action> schedule)
    {
        var expected = ReadFontSettingDraft(owner, field);
        var collection = field.Collection;
        bool Current() => IsFontSettingCurrent(owner, field) && ReferenceEquals(field.Collection, collection)
            && expected.SequenceEqual(ReadFontSettingDraft(owner, field));
        using var disabled = ImGuiScopes.Disabled(IsAuthoringPipelineUpdating || !Current() || !owner.CanAddFontItem(field));
        var supplied = owner.FontCollectionAddEntries(field);
        if (supplied == null)
        { if (Gui.SmallButton("+")) schedule(() => Add(null)); }
        else if (Gui.BeginCombo("##AddFontItem", "+"))
        {
            try { NativeAbstractEntryControl.Choices(supplied, null, entry => schedule(() => Add(entry))); }
            finally { Gui.EndCombo(); }
        }
        void Add(AbstractNodeEntry? entry)
        {
            if (IsAuthoringPipelineUpdating || !Current() || !owner.CanAddFontItem(field)) throw new InvalidOperationException("The font collection changed.");
            var target = owner.AcquireExtensionCommandTarget(field.Node)
                ?? throw new InvalidOperationException("The font collection presenter changed.");
            var transferred = false;
            try
            {
                var entries = owner.FontCollectionAddEntries(field);
                if (entry == null ? entries != null : entries == null || !NativeAbstractEntries.Contains(entries, entry))
                    throw new InvalidOperationException("The font collection factory changed.");
                var command = NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(candidate => candidate.Name == "AddNewItem");
                if (command != null && !command.CanExecute(target.Presenters, entry)) throw new InvalidOperationException("The font item cannot be added.");
                var actualTarget = CertifiedPropertyTarget(owner, field.Node);
                ValidatePendingPropertyStrings();
                if (!IsFontSettingCurrent(owner, field) || !ReferenceEquals(field.Collection, collection)
                    || !actualTarget.SequenceEqual(CertifiedPropertyTarget(owner, field.Node)) || !owner.CanAddFontItem(field))
                    throw new InvalidOperationException("The font collection changed during pending validation.");
                entries = owner.FontCollectionAddEntries(field);
                if (entry == null ? entries != null : entries == null || !NativeAbstractEntries.Contains(entries, entry))
                    throw new InvalidOperationException("The font collection factory changed during pending validation.");
                if (command != null)
                {
                    if (!NodePresenterCombination.Commands(target.Presenters).Contains(command) || !command.CanExecute(target.Presenters, entry))
                        throw new InvalidOperationException("The native font item command changed.");
                    transferred = true; StartEditorExtensionCommand(owner, target, command, entry);
                }
                else owner.AddFontSettingItem(field, entry);
            }
            finally { if (!transferred) ReleaseHierarchyFactoryTarget(target); }
        }
    }
}
