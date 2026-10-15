using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteCommonPresenterCommands(AssetMetadataDocument owner, SpriteCommonSetting setting)
    {
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("##SpriteCommonCommands");
        if (!Gui.BeginPopup("##SpriteCommonCommands")) return;
        try
        {
            var expected = ReadSpriteCommonNumericDraft(owner, setting);
            bool Current() => IsCurrentMetadataControlOwner(owner) && !IsAuthoringPipelineUpdating
                && owner.IsCurrentSpriteCommonSetting(setting)
                && setting.Fields.All(field => IsSpriteSettingDraftCurrent(owner, field))
                && expected.SequenceEqual(ReadSpriteCommonNumericDraft(owner, setting));
            // The original CopyPropertyCommand is DoNotCombine, even for equal values.
            DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, false);
            var text = Gui.GetClipboardTextS() ?? "";
            var canPaste = Current() && owner.CanPasteSpriteCommonSetting(setting, text);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) RunStock(false, false);
            if (DrawNativeMenuItem("Replace", "", false, canPaste)) RunStock(true, false);
            var canReset = Current() && owner.CanResetSpriteCommonSetting(setting);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, canReset)) RunStock(false, true);
            void RunStock(bool replace, bool reset) => Execute(() =>
            {
                if (!Current() || (reset ? !owner.CanResetSpriteCommonSetting(setting) : !owner.CanPasteSpriteCommonSetting(setting, text)))
                    throw new InvalidOperationException("The combined sprite property action target changed.");
                var fields = setting.Fields.ToArray();
                if (setting.First.Type == typeof(UFile)) CancelFailedSpriteSourceRequest(owner, fields);
                ValidatePendingPropertyStrings();
                if (!Current() || (reset ? !owner.CanResetSpriteCommonSetting(setting) : !owner.CanPasteSpriteCommonSetting(setting, text)))
                    throw new InvalidOperationException("The combined sprite property action target changed.");
                using var lease = new RetainedPathLease(this, AcquireSpriteCommonNumericDraft(owner, setting));
                if (setting.First.Type == typeof(UFile))
                {
                    var sources = reset ? fields.Select(field => ((IAssetNode)field.Node).BaseNode is IMemberNode inherited
                            ? inherited.Retrieve() as UFile : throw new InvalidOperationException("The sprite source has no base value.")).ToArray()
                        : Enumerable.Repeat(owner.PrepareAssetPropertyValue(text, typeof(UFile)) as UFile, fields.Length).ToArray();
                    QueueSpriteSourceEdit(owner, fields, sources, reset, reset ? "Reset to base value" : replace ? "Replace property" : "Paste property");
                }
                else if (reset) owner.ResetSpriteCommonSetting(setting);
                else owner.PasteSpriteCommonSetting(setting, text, replace);
            });
            DrawNativePresenterCommands(owner, Current,
                () => AcquireHierarchyFactoryTargets(setting.Fields.Length,
                    index => owner.AcquireExtensionCommandTarget(setting.Fields[index].Node)),
                () =>
                {
                    if (!Current()) throw new InvalidOperationException("The combined sprite property command target changed.");
                    return new EditorAssetCommandMutation(owner.BeginExtensionCommandMutation());
                });
        }
        finally { Gui.EndPopup(); }
    }
}
