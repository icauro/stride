using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawProceduralObjectProperty(ProceduralAssetDocument asset, AssetPropertyField field, HashSet<IObjectNode>? ancestors)
    {
        var entries = asset.PropertyEntries(field.Node);
        var target = field.Node.Target;
        if (target?.IsEnumerable == true || entries.Count == 0 && target == null) return false;
        if (field.Node.Retrieve() is Stride.Core.Yaml.IUnloadable unknown) { DrawUnloadableProperty(unknown); return true; }
        if (target == null) Gui.TextUnformatted(asset.PropertyLabel(field.Node) ?? field.Label);
        if (entries.Count != 0)
        {
            using var disabled = ImGuiScopes.Disabled(!asset.IsPropertyFieldCurrent(field) || !asset.CanEditAssetSettingNode(field.Node) || asset.NumericReadOnly(field.Node)
                || !CanExecuteProceduralFactory(asset, field, entries));
            if (BeginNativeCombo("##Type", NativeAbstractEntryControl.Label(entries, field.Node.Retrieve())))
            {
                try { NativeAbstractEntryControl.Choices(entries, field.Node.Retrieve(), entry => SelectProceduralFactoryEntry(asset, field, entry)); }
                finally { Gui.EndCombo(); }
            }
        }
        if (target == null) return true;
        ancestors ??= [];
        if (!ancestors.Add(target)) return true;
        try
        {
            var inline = asset.InlineProperty(field.Node);
            if (inline != null)
                DrawProceduralPropertyValue(asset, asset.PropertyField(inline, field.Name + "/" + inline.Name) with { Label = field.Label }, ancestors);
            var enabled = target.Members.FirstOrDefault(member => member.Name == "Enabled" && member.Type == typeof(bool) && !asset.PropertyVisible(member));
            if (enabled != null) DrawProceduralPropertyValue(asset, asset.PropertyField(enabled, field.Name + "/Enabled"), ancestors);
            // The native inlined-value header/footer templates contribute no tree indentation.
            if (inline == null) ApplyProceduralExpansion(asset.PropertyExpansion(field.Node));
            var expanded = inline != null || DrawProceduralObjectChildrenHeader(field with { Label = asset.PropertyLabel(field.Node) ?? field.Label });
            var header = Gui.GetCurrentContext().LastItemData;
            if (expanded)
            {
                try
                {
                    DrawProceduralFields(asset, target.Members.Where(member => !ReferenceEquals(member, inline) && asset.PropertyVisible(member))
                        .Select(member => asset.PropertyField(member, field.Name + "/" + member.Name)), ancestors);
                }
                finally { if (inline == null) Gui.TreePop(); Gui.GetCurrentContext().LastItemData = header; }
            }
        }
        finally { ancestors.Remove(target); }
        return true;
    }
    private bool DrawProceduralObjectChildrenHeader(AssetPropertyField field)
        => DrawNativeTreeNode("##Children", ImGuiTreeNodeFlags.DefaultOpen, false, out _, caption: field.Label);
    private void SelectProceduralFactoryEntry(ProceduralAssetDocument asset, AssetPropertyField field, AbstractNodeEntry entry)
        => Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(assetDocument, asset) || !asset.IsPropertyFieldCurrent(field) || !asset.CanEditAssetSettingNode(field.Node)
                || asset.NumericReadOnly(field.Node) || !NativeAbstractEntries.Contains(asset.PropertyEntries(field.Node), entry))
                throw new InvalidOperationException("The native procedural entry is no longer available.");
            var target = asset.AcquireExtensionCommandTarget(field.Node); var transferred = false;
            try
            {
                var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
                if (command != null)
                {
                    StartEditorExtensionCommand(asset, target!, command, entry,
                        () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                    transferred = true;
                }
                else asset.ReplacePropertyEntry(field.Node, entry);
            }
            finally
            {
                if (!transferred && target != null)
                    try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        });
}
