using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Core;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ProjectCreationPresentation(EditorGame game) : IProjectCreationPresentation
    {
        public Vector2 MeasureCaption(string text) => game.MeasureNativeLabel(text);
        public void ValidationMessage(string text) => game.DrawMessageDialogBody("##ProjectValidationError", text, EditorMessageDialogImage.Error);
        public bool ValidationAccept() => game.DrawNativeButton(EditorLocalization.Widget("OK", "Button"));
        public bool ParameterButton(string label, Vector2 size) => game.DrawNativeButton(label, size);
        public bool BeginChoice(string identity, string preview) => game.CanDrawNativeWidgetText
            ? game.BeginNativeCombo(identity, preview) : EditorLiteralTemplateControls.BeginCombo(identity, preview);
        public bool ChoiceOption(string identity, string text, bool selected) => game.DrawNativeLiteralSelectable(identity, text, selected);
        public bool LiteralCheckbox(string identity, string text, ref bool value, PlatformType? platform = null)
        {
            var scale = Gui.GetStyle().FontScaleDpi;
            ImTextureRef? image = null;
            if (platform != null && game.icons?.EnumIcon(platform.Value) is { } key) image = game.icons.Get(key);
            return game.DrawNativeLiteralCheckbox(identity, text, ref value, image, image is null ? Vector2.Zero : new Vector2(16) * scale, 6 * scale);
        }
        public unsafe void ParameterHeading(string text)
        {
            Gui.PushFont(Gui.GetFont(), 16);
            try { game.DrawNativeLabel(text); }
            finally { Gui.PopFont(); }
        }
        public void TemplateRow(ProjectTemplateChoice choice, Vector2 minimum, Vector2 maximum)
            => game.PaintTemplateRow(choice.Description.Name, choice.Description, minimum, maximum);

        public unsafe void Heading(string text)
        {
            var bold = game.PushPackagePropertiesBoldFont(true);
            try { game.DrawNativeLabel(text); }
            finally { if (bold) Gui.PopFont(); }
        }

        public bool Checkbox(string label, ref bool value, PlatformType? platform = null)
        {
            if (platform == null) return game.DrawNativeCheckbox(label, ref value);
            var scale = Gui.GetStyle().FontScaleDpi;
            ImTextureRef? image = null;
            if (game.icons?.EnumIcon(platform.Value) is { } key) image = game.icons.Get(key);
            return game.DrawNativeCheckbox(label, ref value, image, new Vector2(16) * scale, 6 * scale);
        }
    }
}
