using Microsoft.CodeAnalysis;

namespace Stride.Editor.ImGui;

internal sealed partial class ScriptSemanticWorkspace
{
    private Dictionary<ProjectId, ScriptAnalyzerPolicy> analyzerPolicies = [];
    private ScriptAnalyzerRun CreateAnalyzerRun(Project project, CancellationToken cancellation, bool liveAnalysis = false)
        => new(project, cancellation, liveAnalysis, analyzerPolicies.GetValueOrDefault(project.Id));
}
