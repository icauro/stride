using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredFontSetting(AssetMetadataDocument owner, FontSettingField field, string label)
    {
        var extensions = owner.SessionHistory?.Extensions;
        if (extensions?.HasPrimitiveEditor(field.Type) != true) return false;
        if (!BeginInspectorPropertyRow(label)) return true;
        try
        {
            using var lease = new RetainedPathLease(this, AcquireFontSettingDraft(owner, field));
            var expected = ReadFontSettingDraft(owner, field);
            var rendering = true;
            bool Current() => rendering && !IsAuthoringPipelineUpdating && IsFontSettingCurrent(owner, field)
                && expected.SequenceEqual(ReadFontSettingDraft(owner, field));
            void Validate()
            {
                if (!Current()) throw new InvalidOperationException("The font property editor context changed.");
                var target = new object?[] { field.Node, field.Node.Parent.Retrieve(), field.Node.Retrieve(),
                    field.Node.Retrieve() is DateTime beforeDate ? beforeDate.Kind : null, owner.Path,
                    sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, extensions.Version };
                ValidatePendingPropertyStrings();
                if (!rendering || IsAuthoringPipelineUpdating || !IsFontSettingCurrent(owner, field)
                    || !target.SequenceEqual(new object?[] { field.Node, field.Node.Parent.Retrieve(), field.Node.Retrieve(),
                        field.Node.Retrieve() is DateTime afterDate ? afterDate.Kind : null, owner.Path,
                        sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, extensions.Version }))
                    throw new InvalidOperationException("The font property changed during pending validation.");
                expected = ReadFontSettingDraft(owner, field);
            }
            try
            {
                extensions.TryDrawPrimitive(new(field.Type, owner.ReadFontSetting(field), label,
                    Current() && owner.CanEditFontSetting(field), value =>
                    {
                        if (!Current() || !owner.CanEditFontSetting(field)) throw new InvalidOperationException("The font property is read-only or changed.");
                        if (ContentAssetReference.AreEqual(field.Node.Retrieve(), value)) return;
                        Validate();
                        CommitFontSettingDraft(owner, field, value);
                        expected = ReadFontSettingDraft(owner, field);
                    }, () =>
                    {
                        Validate(); owner.EndFontSettingEdit(); expected = ReadFontSettingDraft(owner, field);
                    }, (name, parameter) =>
                    {
                        if (!Current()) throw new InvalidOperationException("The font property command context changed.");
                        var target = owner.AcquireExtensionCommandTarget(field.Node)
                            ?? throw new InvalidOperationException("The font property has no native command target.");
                        var transferred = false;
                        try
                        {
                            var command = NodePresenterCombination.Commands(target.Presenters)
                                .FirstOrDefault(candidate => candidate.Name == name && candidate.CanExecute(target.Presenters, parameter))
                                ?? throw new InvalidOperationException("The font property command is not available.");
                            Validate();
                            if (!NodePresenterCombination.Commands(target.Presenters).Contains(command) || !command.CanExecute(target.Presenters, parameter))
                                throw new InvalidOperationException("The font property command changed during pending validation.");
                            transferred = true; StartEditorExtensionCommand(owner, target, command, parameter);
                        }
                        finally { if (!transferred) ReleaseHierarchyFactoryTarget(target); }
                    }));
            }
            finally { rendering = false; }
            DrawAssetSettingResetToBase(owner, field.Node);
        }
        finally { Gui.EndTable(); }
        return true;
    }
}
