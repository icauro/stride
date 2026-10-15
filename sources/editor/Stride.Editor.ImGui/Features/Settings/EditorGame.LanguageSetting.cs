using Stride.Core.Assets.Editor;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly SupportedLanguage[] EditorLanguages = Enum.GetValues<SupportedLanguage>();
    private static string LanguageName(SupportedLanguage language) => language switch
    {
        SupportedLanguage.MachineDefault => "Default",
        SupportedLanguage.ChineseSimplified => "Chinese (Simplified)",
        _ => language.ToString(),
    };

    private void DrawLanguageSetting()
    {
        BeginSettingsProperty("Language");
        Gui.SetNextItemWidth(-1);
        if (Gui.BeginCombo("##Value", LanguageName(settingsLanguage)))
        {
            foreach (var language in EditorLanguages)
                if (Gui.Selectable(LanguageName(language), language == settingsLanguage)) settingsLanguage = language;
            Gui.EndCombo();
        }
        Gui.PopID();
    }
}
