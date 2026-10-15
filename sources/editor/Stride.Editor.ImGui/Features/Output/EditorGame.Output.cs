using System.Text;
using Hexa.NET.ImGui;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorOutputLog outputLog => Services.Output;
    private OutputPanel? outputPanelState;
    private OutputPanel outputPanel => outputPanelState ??= new(outputLog, diagnostic => Execute(() => NavigateOutputDiagnostic(diagnostic)),
        () => preferences?.Current.Theme ?? EditorTheme.ExpressionDark, () => icons, CanActivateOutputAfterRecovery);
    private ref bool focusOutput => ref outputPanel.FocusRequested;
    private ref bool showOutput => ref outputPanel.Visible;
    private void NavigateOutputDiagnostic(BuildDiagnostic diagnostic)
    {
        var expected = workspace;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        if (!ReferenceEquals(workspace, expected))
            throw new InvalidOperationException("The project changed while finishing pending edits.");
        if (NavigateScriptDiagnostic(diagnostic)) return;
        var asset = workspace?.Assets.FirstOrDefault(candidate => string.Equals(candidate.Path, diagnostic.Path, StringComparison.OrdinalIgnoreCase));
        if (asset != null) OpenBrowserAsset(asset);
    }
    private void ReportProjectLoadState(ProjectLoadState state, ProjectLoadProgressCapture progress)
    {
        progress.Append(state);
        if (ReferenceEquals(Volatile.Read(ref projectLoadProgress), progress)) Volatile.Write(ref projectLoadState, state);
        RecordBuildOutput(state);
    }
    private void RecordBuildOutput(ProjectLoadState state)
    {
        if (state.CompilerRun is { } run && state.CompilerEvent != CompilerRunEvent.None) outputLog.RecordCompilerLifecycle(run, state.CompilerEvent);
        if (state.AssetDiagnostic is { } asset)
        {
            BuildDiagnostic? location = asset.File is { Length: > 0 } path && System.IO.Path.IsPathFullyQualified(path)
                ? new(path, Math.Max(1, asset.Line), Math.Max(1, asset.Column), asset.Severity, "", asset.Text) : null;
            outputLog.Append("Build", asset.Severity, asset.Module, asset.Text, asset.Exception, location, asset.AssetId, state.CompilerRun, asset.AssetUrl, asset.ExceptionType);
        }
        if (state.OutputLine is { } line)
        {
            var level = state.Diagnostic?.Level ?? BuildOutputLevel(line);
            outputLog.Append("Build", level, state.Phase, line, diagnostic: state.Diagnostic, compilerRun: state.CompilerRun);
        }
    }
    private static LogMessageType BuildOutputLevel(string line)
    {
        var text = line.AsSpan().TrimStart();
        if (text.StartsWith("[", StringComparison.Ordinal) && text.IndexOf("]:", StringComparison.Ordinal) is var moduleEnd && moduleEnd >= 0)
            text = text[(moduleEnd + 2)..].TrimStart();
        if (text.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("error ", StringComparison.OrdinalIgnoreCase)
            || text.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0) return LogMessageType.Error;
        if (text.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("warning ", StringComparison.OrdinalIgnoreCase)
            || text.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase) >= 0) return LogMessageType.Warning;
        if (text.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)) return LogMessageType.Fatal;
        if (text.StartsWith("debug:", StringComparison.OrdinalIgnoreCase)) return LogMessageType.Debug;
        if (text.StartsWith("verbose:", StringComparison.OrdinalIgnoreCase)) return LogMessageType.Verbose;
        return LogMessageType.Info;
    }
}
