using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Assets;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Gui = Hexa.NET.ImGui.ImGui;


namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RemoveGameSettingDrafts(IEditorDocument document)
        => AbandonPendingPropertyStrings(document);
    private void DrawGameSettings(AssetMetadataDocument settings)
    {
        if (settings.GameSettings == null) return;
        Gui.PushID("GameSettings");
        pendingGameSettingCollectionAction = null;
        Action? structureAction = null;
        foreach (var field in settings.GameSettingFields.Where(settings.IsPresentedGameSettingVisible).ToArray())
        {
            if (field.Node.Name == nameof(GameSettingsAsset.Defaults))
            {
                foreach (var configuration in settings.GameSettingConfigurations.Where(settings.IsPresentedGameConfigurationVisible).ToArray())
                {
                    Gui.PushID(configuration.Index.Int);
                    var open = Gui.CollapsingHeader(settings.PresentedGameConfigurationLabel(configuration), ImGuiTreeNodeFlags.DefaultOpen);
                    DrawGameConfigurationContext(settings, configuration, command => structureAction = command,
                        !settings.IsExtensionPropertyReadOnly(field.Node) && settings.CanEditPresentedGameConfiguration(configuration));
                    if (open)
                    {
                        if (configuration.Value is Stride.Core.Yaml.IUnloadable unknown) DrawUnloadableProperty(unknown);
                        else DrawGameSettingFields(settings, configuration.Fields);
                    }
                    Gui.PopID();
                }
                Gui.TextUnformatted(EditorLocalization.Text("Add configuration")); Gui.SameLine();
                using (ImGuiScopes.Disabled(!settings.CanEditGameSettings || settings.IsExtensionPropertyReadOnly(field.Node)))
                    if (Gui.SmallButton("+")) Gui.OpenPopup("AddConfiguration");
                if (Gui.BeginPopup("AddConfiguration"))
                {
                    using var addDisabled = ImGuiScopes.Disabled(!settings.CanEditGameSettings || settings.IsExtensionPropertyReadOnly(field.Node));
                    var entries = settings.GameConfigurationEntries();
                    NativeAbstractEntryControl.Choices(entries, null, entry => structureAction = () => SelectNativeFactoryEntry(settings,
                        new Stride.Core.Quantum.NodeAccessor(field.Node.Target!, Stride.Core.Quantum.NodeIndex.Empty), entry,
                        settings.GameConfigurationEntries, () => settings.AddGameConfigurationEntry(entry), "AddNewItem"));
                    Gui.EndPopup();
                }
            }
        }
        DrawGameSettingFields(settings, settings.GameSettingFields.Where(field => field.Node.Name != nameof(GameSettingsAsset.Defaults)).ToArray());
        Gui.PopID();
        structureAction ??= pendingGameSettingCollectionAction; pendingGameSettingCollectionAction = null;
        if (structureAction != null) { UseTextureSettingsCommandContext(); Execute(structureAction); }
    }
    private void DrawGameSettingFields(AssetMetadataDocument settings, IReadOnlyList<GameSettingField> fields)
    {
        DrawPresentedAssetRows(settings, fields.Where(settings.IsPresentedGameSettingVisible).Select(field =>
            new PresentedAssetRow(field.Node, field.Category, field.Order, field.CategoryMetadata?.Expand ?? ExpandRule.Always,
                () => DrawGameSetting(settings, field), field.CategoryMetadata?.Order)));
    }
    private void DrawGameSetting(AssetMetadataDocument settings, GameSettingField field, string? label = null, bool inlined = false)
    {
        // InlineMemberNodeUpdater hides the child from ordinary tree enumeration;
        // its original parent template still binds that actual child's NodeValue.
        if (!settings.IsCurrentGameSettingField(field) || !inlined && !settings.IsPresentedGameSettingVisible(field)) return;
        label ??= settings.PresentedGameSettingLabel(field);
        Gui.PushID(field.Key);
        try
        {
            var value = field.Node.Retrieve();
            if (CurveBinding.IsSupported(value)
                && icons!.Button("edit-curve", EditorLocalization.Text("Edit curve", "ToolTip"), 16)) OpenCurveEditor(settings, field.Node, label);
            // An exact registered whole-field editor wins over child and collection templates.
            if (TryDrawGameFieldValueDraft(settings, field, label)) return;
            if (field.IsCollection) { DrawGameSettingCollection(settings, field); return; }
            var nativeEntries = settings.GameSettingEntries(field);
            if (nativeEntries.Count > 0 && field.CanAssign)
            {
                using var disabled = ImGuiScopes.Disabled(!settings.CanEditPresentedGameSetting(field) || IsAuthoringPipelineUpdating);
                if (BeginNativeCombo(label, NativeAbstractEntryControl.Label(nativeEntries, value)))
                {
                    try { NativeAbstractEntryControl.Choices(nativeEntries, value, entry => SelectNativeFactoryEntry(settings, field.Node, entry,
                        () => settings.GameSettingEntries(field), () => settings.ChangeGameSettingEntry(field, entry)),
                        entry => CanExecuteNativeFactory(settings, new(field.Node, Stride.Core.Quantum.NodeIndex.Empty), [entry])); }
                    finally { Gui.EndCombo(); }
                }
            }
            DrawAssetSettingResetContext(settings, field.Node);
            if (!settings.IsCurrentGameSettingField(field)) return;
            if (value is Stride.Core.Yaml.IUnloadable unknown)
            {
                if (nativeEntries.Count == 0) DrawAssetSettingLabel(settings, field.Node, label);
                DrawUnloadableProperty(unknown);
                return;
            }
            if (field.Children.Count > 0)
            {
                var inline = field.InlineMember; var enabled = field.EnabledMember;
                var expand = inline != null ? field.InlineExpansion : field.Expand;
                if (expand != ExpandRule.Auto) Gui.SetNextItemOpen(expand is ExpandRule.Always or ExpandRule.Once, ImGuiCond.Once);
                var open = Gui.TreeNodeEx(inline != null || field.Types.Count > 0 ? "##Properties" : label, ImGuiTreeNodeFlags.DefaultOpen);
                DrawAssetSettingResetContext(settings, field.Node);
                if (enabled != null)
                {
                    Gui.SameLine(); var next = (bool)(enabled.Node.Retrieve() ?? false);
                    using var disabled = ImGuiScopes.Disabled(!settings.CanEditPresentedGameSetting(enabled) || IsAuthoringPipelineUpdating);
                    if (DrawNativeCheckbox("##Enabled", ref next)) Execute(() => ApplyGameFieldDiscrete(settings, enabled, next));
                }
                if (inline != null) { Gui.SameLine(); DrawGameSetting(settings, inline, label, inlined: true); }
                if (open)
                {
                    DrawGameSettingFields(settings, field.Children.Where(member => !ReferenceEquals(member, inline) && !ReferenceEquals(member, enabled)).ToArray());
                    Gui.TreePop();
                }
                return;
            }
            if (!BeginInspectorPropertyRow(label, false)) return;
            try
            {
                Gui.TableSetColumnIndex(0); DrawAssetSettingLabel(settings, field.Node, label);
                Gui.TableSetColumnIndex(1); Gui.SetNextItemWidth(-1);
                using (ImGuiScopes.Disabled(!settings.CanEditPresentedGameSetting(field) || IsAuthoringPipelineUpdating))
                    if (!TryDrawVisualScriptSymbolReference(settings, field, value)) Gui.TextUnformatted(value?.ToString() ?? EditorLocalization.Text("(null)"));
                DrawAssetSettingResetContext(settings, field.Node);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void ApplyGameFieldDiscrete(AssetMetadataDocument owner, GameSettingField field, object? value)
    {
        if (!IsCurrentMetadataControlOwner(owner) || !owner.CanEditPresentedGameSetting(field) || IsAuthoringPipelineUpdating)
            throw new InvalidOperationException("The setting is read-only or changed.");
        if (ContentAssetReference.AreEqual(field.Node.Retrieve(), value)) return;
        var before = CertifiedPropertyTarget(owner, field.Node);
        ValidatePendingPropertyStrings();
        if (!IsCurrentMetadataControlOwner(owner) || !owner.CanEditPresentedGameSetting(field) || IsAuthoringPipelineUpdating
            || !before.SequenceEqual(CertifiedPropertyTarget(owner, field.Node)))
            throw new InvalidOperationException("The setting changed during pending validation.");
        using var target = new RetainedPathLease(this, owner.AcquireExtensionCommandTarget(field.Node)
            ?? throw new InvalidOperationException("The settings property target changed."));
        using var view = new RetainedPathLease(this, AcquirePropertyStringOwner(owner));
        owner.UpdateGameFieldValue(field, value);
    }
}
