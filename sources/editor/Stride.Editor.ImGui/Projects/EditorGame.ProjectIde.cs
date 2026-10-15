using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string settingsIde = "Default IDE";
    private Task? projectIdeOpen;
    private CancellationTokenSource? projectIdeCancellation;
    private void DrawOpenInIdeCommand()
    {
        var available = workspace?.Session.SolutionPath is { } solution && File.Exists(solution.ToOSPath());
        if (Gui.MenuItem(EditorLocalization.Widget("Open in IDE", "ToolTip"), "", false, available && projectIdeOpen == null))
            Execute(StartProjectIdeOpen);
    }
    private void StartProjectIdeOpen()
    {
        projectIdeCancellation = new();
        try { projectIdeOpen = ProjectIdeLauncher.OpenAsync(workspace!, preferences?.Current.DefaultIDE ?? "Default IDE", projectIdeCancellation.Token); }
        catch { projectIdeCancellation.Dispose(); projectIdeCancellation = null; throw; }
    }
    private void PumpProjectIdeOpen()
    {
        if (projectIdeOpen is not { IsCompleted: true } completed) return;
        projectIdeOpen = null;
        projectIdeCancellation?.Dispose(); projectIdeCancellation = null;
        if (completed.IsCanceled) return;
        Execute(() => completed.GetAwaiter().GetResult());
    }
    private void StopProjectIdeOpen()
    {
        var task = projectIdeOpen; var cancellation = projectIdeCancellation;
        projectIdeOpen = null; projectIdeCancellation = null;
        cancellation?.Cancel();
        if (task == null) { cancellation?.Dispose(); return; }
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted) Console.Error.WriteLine("IDE activation ended during close: " + completed.Exception);
            cancellation?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    private void DrawIdeSettings()
    {
        Gui.SetNextItemWidth(UiScale(280));
        if (!Gui.BeginCombo("Default IDE", settingsIde)) return;
        foreach (var ide in ProjectIdeLauncher.Available)
            if (Gui.Selectable(ide.DisplayName, ide.DisplayName == settingsIde)) settingsIde = ide.DisplayName;
        Gui.EndCombo();
    }
}
