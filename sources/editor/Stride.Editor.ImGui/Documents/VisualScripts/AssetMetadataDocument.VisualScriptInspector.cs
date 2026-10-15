using Stride.Assets.Scripts;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<Block, IReadOnlyList<GameSettingField>> visualScriptInspectorFields = new(ReferenceEqualityComparer.Instance);
    private bool HasVisualScriptInspector => this is VisualScriptDocument && gameSettingsGraph != null;

    internal IReadOnlyList<GameSettingField> GetVisualScriptBlockFields(Block block)
    {
        if (this is not VisualScriptDocument visual || !visual.Asset.Methods.Any(method => method.Blocks.Values.Contains(block)))
            throw new InvalidOperationException("The block has no current visual-script owner.");
        if (gameSettingsGraph == null)
        {
            gameSettingsGraph = EnsureSettingsAuthoringGraph(out gameSettingsItem);
        }
        if (!visualScriptInspectorFields.TryGetValue(block, out var fields))
        {
            fields = ReadVisualScriptBlockFields(visual, block);
            visualScriptInspectorFields.Add(block, fields);
        }
        return fields;
    }
    private IReadOnlyList<GameSettingField> ReadVisualScriptBlockFields(VisualScriptDocument visual, Block block)
        => ReadGameSettingsFields(visual.ObjectNode(block), "VisualBlock." + block.Id, 0, new HashSet<IObjectNode>(),
            rootMemberFilter: node => node.MemberDescriptor.MemberInfo.DeclaringType != typeof(Block));

    internal void RefreshVisualScriptInspectorPresentation()
    {
        if (HasVisualScriptInspector) RefreshGameSettingsFields();
    }
    private void RefreshVisualScriptInspectorFields()
    {
        var visual = (VisualScriptDocument)this;
        foreach (var block in visualScriptInspectorFields.Keys.ToArray())
            if (!visual.Asset.Methods.Any(method => method.Blocks.Values.Contains(block))) visualScriptInspectorFields.Remove(block);
            else visualScriptInspectorFields[block] = ReadVisualScriptBlockFields(visual, block);
        gameSettingFields = visualScriptInspectorFields.Values.SelectMany(fields => fields).ToArray();
        gameSettingConfigurations = [];
    }
    private bool RecordVisualScriptInspectorChange(string title, Action undo, Action redo)
    {
        if (!HasVisualScriptInspector) return false;
        // Native settings controls have already performed their preview mutation.
        // Restore that actual node state, then apply through the visual producer's
        // one Quantum journal so slots, links and generated source share recovery.
        undo();
        ((VisualScriptDocument)this).ApplyVisualInspectorChange(redo, title);
        RefreshGameSettingsFields();
        return true;
    }
}
