using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ImGuiScopes.WindowScope BeginProjectLoadProgressWindow()
    {
        using var contentMargin = ImGuiScopes.Style(ImGuiStyleVar.WindowPadding, UiScale(20, 20));
        return ImGuiScopes.Window(EditorLocalization.Widget("Project loading"), ImGuiWindowFlags.NoCollapse);
    }

    private void DrawProjectLoadProgress(ProjectLoadState? state, ProjectLoadProgressCapture? progress,
        CancellationTokenSource? cancellation, string fallbackPhase, bool recordCancel)
    {
        var viewport = Gui.GetMainViewport();
        Gui.SetNextWindowSize(NVector2.Min(UiScale(640, 480), viewport.WorkSize), ImGuiCond.FirstUseEver);
        Gui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new NVector2(0.5f));
        using (var window = BeginProjectLoadProgressWindow())
        {
            if (window.DrawContents)
            {
                DrawNativeLabel(EditorLocalization.Text(state?.Phase ?? fallbackPhase));
                Gui.ProgressBar((float)-Gui.GetTime(), new NVector2(-1, UiScale(25)), "");
                var cancelTextSize = MeasureNativeLabel(EditorLocalization.Text("Cancel", "Button"));
                var cancelSize = cancelTextSize + UiScale(32, 8);
                var footer = cancelSize.Y + Gui.GetStyle().ItemSpacing.Y;
                using (var output = ImGuiScopes.Child("Build output", new NVector2(0, Math.Max(1, Gui.GetContentRegionAvail().Y - footer)),
                    ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
                {
                    if (output.DrawContents)
                    {
                        using var logFont = ImGuiScopes.Font(UiScale(11));
                        var messages = progress?.Snapshot() ?? "";
                        DrawSelectableText("project-loading-detail", messages.Length == 0 ? state?.Detail ?? "" : messages, size: NVector2.Max(NVector2.One, Gui.GetContentRegionAvail()), followTail: messages.Length != 0, nativeFontFamily: "Consolas");
                    }
                }
                var label = EditorLocalization.Widget("Cancel###Cancel loading", "Button");
                var width = cancelSize.X;
                Gui.SetCursorPosX(Gui.GetCursorPosX() + Math.Max(0, Gui.GetContentRegionAvail().X - width));
                using var disabled = ImGuiScopes.Disabled(cancellation == null || cancellation.IsCancellationRequested);
                if (DrawNativeButton(label, cancelSize)) cancellation?.Cancel();
                if (recordCancel && Controls.Enabled) Controls.Record("loading/cancel");
            }
        }
    }
}
