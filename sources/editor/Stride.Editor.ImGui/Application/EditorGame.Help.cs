using System.Diagnostics;
using Stride.GameStudio.Helpers;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawHelpMenu()
    {
        if (!BeginNativeMenu(EditorLocalization.Widget("Help", "Menu"))) return;
        try
        {
            if (DrawNativeMenuItem(EditorLocalization.Widget("Online documentation", "Menu"))) Execute(() => OpenHelpPage(StrideGameStudio.DocumentationUrl));
            Gui.Separator();
            if (DrawNativeMenuItem(EditorLocalization.Widget("Questions and answers", "Menu"))) Execute(() => OpenHelpPage(StrideGameStudio.AnswersUrl));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Report an issue...", "Menu"))) Execute(() => OpenHelpPage(StrideGameStudio.ReportIssueUrl));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Community forums", "Menu"))) Execute(() => OpenHelpPage(StrideGameStudio.ForumsUrl));
            Gui.Separator();
            if (DrawNativeMenuItem(EditorLocalization.Widget("Show debug window", "Menu"), "Ctrl+Shift+D")) OpenDebugWindow();
            if (DrawNativeMenuItem(EditorLocalization.Widget("About...", "Menu"))) openAbout = true;
        }
        finally { Gui.EndMenu(); }
    }

    private static void OpenHelpPage(string address)
    {
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
    }
}
