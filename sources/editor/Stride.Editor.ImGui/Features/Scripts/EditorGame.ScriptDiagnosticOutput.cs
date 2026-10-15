using Microsoft.CodeAnalysis;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private HashSet<ScriptDiagnosticReport> reportedScriptDiagnostics = [];
    private void ReportScriptDiagnostics(ScriptDiagnosticReport[] diagnostics)
    {
        var current = diagnostics.ToHashSet();
        foreach (var report in current)
        {
            if (reportedScriptDiagnostics.Contains(report)) continue;
            var level = report.Severity switch { DiagnosticSeverity.Error => LogMessageType.Error, DiagnosticSeverity.Warning => LogMessageType.Warning, _ => LogMessageType.Info };
            var message = report.Code.Length == 0 ? report.Message : report.Code + ": " + report.Message;
            BuildDiagnostic? location = null;
            if (report.Path is { } path)
            {
                message = path + "(" + report.Line + "," + report.Column + "): " + message;
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) location = new BuildDiagnostic(path, report.Line, report.Column, level, report.Code, report.Message);
            }
            outputLog.Append("Project", level, "Script", message, diagnostic: location);
        }
        reportedScriptDiagnostics = current;
    }
}
