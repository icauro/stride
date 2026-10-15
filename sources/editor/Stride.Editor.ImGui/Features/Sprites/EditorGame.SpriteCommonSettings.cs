using Stride.Core.IO;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly string[] SpriteRectangleAxes = ["X", "Y", "Width", "Height"];
    private static readonly string[] SpriteVectorAxes = ["X", "Y", "Z", "W"];
    private void DrawSpriteCommonSetting(AssetMetadataDocument document, SpriteCommonSetting setting)
    {
            if (setting.Fields.All(member => !document.IsExtensionPropertyVisible(member.Node))) return;
            setting.Refresh(document.SpriteSettingsEditVersion);
            var first = setting.First; Gui.PushID(first.Node.Name);
            try
            {
            DrawSpritePropertyLabel(document, first);
            DrawSpriteCommonPresenterCommands(document, setting); Gui.SetNextItemWidth(-1);
            if (sessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) == true)
            {
                TryDrawSpritePrimitive(first.Type, SpritePropertyLabel(document, first), () => ReadSpriteCommonNumericDraft(document, setting),
                    () => CanEditSpriteCommonNumericDraft(document, setting), create =>
                    {
                        var value = create(first.Node.Retrieve());
                        if (first.Type == typeof(UFile) && first.Node.Name == nameof(Stride.Assets.Sprite.SpriteInfo.Source)) QueueSpriteSourceEdit(document, setting.Fields, Enumerable.Repeat(value as UFile, setting.Fields.Length).ToArray());
                        else if (value == null) document.ApplyInheritedAssetMutation(() => { foreach (var field in setting.Fields) field.Node.Update(null); }, "Edit " + first.Label);
                        else { document.UpdateSpriteCommonValue(setting, _ => value); document.EndSpriteCommonEdit(); }
                    }, () => AcquireSpriteCommonNumericDraft(document, setting), setting.Fields.Length);
                return;
            }
            if (AssetSettingNumericMetadata.Supports(SpriteCoordinateType(first.Type)))
            {
                DrawSpriteCommonNumericDraft(document, setting);
                return;
            }
            if (first.Type == typeof(string))
            {
                DrawSpriteCommonNameDraft(document, setting);
                return;
            }
            if (first.Type == typeof(UFile))
            {
                DrawSpriteCommonSource(document, setting);
                return;
            }
            var editable = true;
            for (var index = 0; index < setting.Fields.Length; index++) if (!document.CanEditSpriteSetting(setting.Fields[index])) { editable = false; break; }
            using var disabled = ImGuiScopes.Disabled(!editable && first.Type != typeof(Color) && first.Type != typeof(bool) && !first.Type.IsEnum);
            if (first.Type == typeof(bool) || first.Type.IsEnum) DrawSpriteCommonDiscreteSetting(document, setting);
            else if (first.Type == typeof(Color)) DrawSpriteCommonColor(document, setting, editable);
            else
            {
                DrawSelectableText("##Value", setting.IsMixed ? DifferentPropertyValues : setting.Values[0]?.ToString() ?? "");
            }
            }
            finally { Gui.PopID(); }
    }
    private static object WithSpriteCoordinate(object? before, Type type, int axis, object number)
    {
        if (type == typeof(Vector2)) { var vector = (Vector2)before!; if (axis == 0) vector.X = (float)number; else vector.Y = (float)number; return vector; }
        if (type == typeof(Vector4))
        { var vector = (Vector4)before!; switch (axis) { case 0: vector.X = (float)number; break; case 1: vector.Y = (float)number; break; case 2: vector.Z = (float)number; break; default: vector.W = (float)number; break; } return vector; }
        if (type == typeof(Rectangle))
        { var region = (Rectangle)before!; switch (axis) { case 0: region.X = (int)number; break; case 1: region.Y = (int)number; break; case 2: region.Width = (int)number; break; default: region.Height = (int)number; break; } return region; }
        return number;
    }
}
