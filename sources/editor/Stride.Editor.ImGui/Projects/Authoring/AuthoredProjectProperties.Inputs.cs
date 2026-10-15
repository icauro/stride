using Stride.Core.Assets;
namespace Stride.Editor.ImGui;

internal sealed record ProjectEvaluatedBuildInputs(string[] Paths, bool? RunAnalyzers, bool? RunAnalyzersDuringLiveAnalysis,
    string[]? ImportWatchDirectories = null);

internal sealed partial class AuthoredProjectProperties
{
    public ProjectEvaluatedBuildInputs EvaluateBuildInputs(string configuration, string framework)
    {
        if (projectPath == null) throw new InvalidOperationException("The project path is required for input evaluation.");
        var evaluated = new Microsoft.Build.Evaluation.Project(root, EvaluationProperties(configuration, framework), null, collection);
        try
        {
            var directory = Path.GetDirectoryName(projectPath)!;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectPath };
            var importDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var import in evaluated.Imports) paths.Add(Path.GetFullPath(import.ImportedProject.FullPath));
            foreach (var origin in evaluated.Imports.Select(import => import.ImportedProject).Append(root).Distinct())
                foreach (var import in origin.Imports)
                {
                    var originDirectory = Path.GetDirectoryName(origin.FullPath)!;
                    var expression = import.Project.Replace("$(MSBuildThisFileDirectory)", originDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileFullPath)", origin.FullPath, StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileName)", Path.GetFileNameWithoutExtension(origin.FullPath), StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileExtension)", Path.GetExtension(origin.FullPath), StringComparison.OrdinalIgnoreCase);
                    foreach (var expanded in evaluated.ExpandString(expression).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.Contains("@(", StringComparison.Ordinal) || expanded.Contains("%(", StringComparison.Ordinal)) continue;
                        var absolute = Path.GetFullPath(expanded, originDirectory);
                        var wildcard = absolute.IndexOfAny(['*', '?']);
                        if (wildcard >= 0)
                        {
                            var separator = absolute.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], wildcard);
                            if (separator >= 0) importDirectories.Add(absolute[..(separator + 1)]);
                        }
                        else paths.Add(absolute);
                    }
                }
            foreach (var type in new[] { "Compile", "AdditionalFiles", "Analyzer", "EditorConfigFiles", "GlobalAnalyzerConfigFiles" })
                foreach (var item in evaluated.GetItems(type)) paths.Add(Path.GetFullPath(item.EvaluatedInclude, directory));
            foreach (var item in evaluated.GetItems("Reference"))
                if (item.GetMetadataValue("HintPath") is { Length: > 0 } hint) paths.Add(Path.GetFullPath(hint, directory));
            foreach (var path in evaluated.GetPropertyValue("MSBuildAllProjects").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                paths.Add(Path.GetFullPath(path, directory));
            foreach (var property in new[] { "ProjectAssetsFile", "ProjectAssetsCacheFile" })
                if (evaluated.GetPropertyValue(property) is { Length: > 0 } path) paths.Add(Path.GetFullPath(path, directory));
            bool? Enabled(string property) => bool.TryParse(evaluated.GetPropertyValue(property), out var enabled) ? enabled : null;
            return new(paths.ToArray(), Enabled("RunAnalyzers"), Enabled("RunAnalyzersDuringLiveAnalysis"), importDirectories.ToArray());
        }
        finally { collection.UnloadProject(evaluated); }
    }
}
