using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanPasteSpriteCommonSetting(SpriteCommonSetting setting, string text)
        => IsCurrentSpriteCommonSetting(setting) && setting.Fields.All(CanEditSpriteSetting)
            && CanPasteAssetProperties(setting.Fields.Select(field => field.Node).ToArray(), text);
    internal void PasteSpriteCommonSetting(SpriteCommonSetting setting, string text, bool replace)
    {
        CommitEdits();
        if (!CanPasteSpriteCommonSetting(setting, text)) throw new InvalidOperationException("The clipboard cannot be pasted into these sprite properties.");
        if (setting.First.Type == typeof(Stride.Core.IO.UFile)) throw new InvalidOperationException("Sprite sources require decoded native assignment.");
        var value = PrepareAssetPropertyValue(text, setting.First.Type);
        void Apply()
        {
            if (value == null)
                ApplyInheritedAssetMutation(() =>
                {
                    foreach (var field in setting.Fields) field.Node.Update(null);
                }, replace ? "Replace property" : "Paste property");
            else { UpdateSpriteCommonValue(setting, _ => value); EndSpriteCommonEdit(); }
        }
        if (SessionHistory == null) Apply();
        else
        {
            using var captured = SessionHistory.CaptureOperationsAtomic(Apply);
            captured.Publish(SessionHistory, replace ? "Replace property" : "Paste property");
        }
    }
    internal bool CanResetSpriteCommonSetting(SpriteCommonSetting setting)
        => IsCurrentSpriteCommonSetting(setting) && setting.Fields.All(field => CanEditSpriteSetting(field) && CanResetAssetSettingNode(field.Node));
    internal void ResetSpriteCommonSetting(SpriteCommonSetting setting)
    {
        CommitEdits();
        if (!CanResetSpriteCommonSetting(setting)) throw new InvalidOperationException("The combined sprite property has no editable base value.");
        if (setting.First.Type == typeof(Stride.Core.IO.UFile)) throw new InvalidOperationException("Sprite sources require decoded native assignment.");
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var field in setting.Fields) ((IAssetNode)field.Node).ResetOverrideRecursively();
        }, "Reset to base value");
    }
}
