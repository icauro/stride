using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed record ScriptProjectWatchTarget(string OutputPath, bool? RunAnalyzers, bool? RunAnalyzersDuringLiveAnalysis);
internal sealed record ScriptProjectWatchEvaluation(string[] Paths, bool? RunAnalyzers, bool? RunAnalyzersDuringLiveAnalysis,
    ScriptProjectWatchTarget[]? Targets = null);

/// <summary>Evaluated project origins for observation; never creates compilation references.</summary>
internal static class ScriptProjectWatchPaths
{
    public static async Task<string[]> Read(string project, string configuration, string framework, CancellationToken cancellation)
        => (await Evaluate(project, configuration, framework, cancellation).ConfigureAwait(false)).Paths;

    public static async Task<ScriptProjectWatchEvaluation> Evaluate(string project, string configuration, string framework, CancellationToken cancellation)
        => await Evaluate(project, configuration, framework, cancellation, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);

    private static async Task<ScriptProjectWatchEvaluation> Evaluate(string project, string configuration, string framework,
        CancellationToken cancellation, HashSet<string> visited)
    {
        project = Path.GetFullPath(project);
        if (!visited.Add(project + "\n" + framework)) return new([project], null, null);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "msbuild", project, "-nologo", "-getProperty:MSBuildAllProjects,ProjectAssetsFile,ProjectAssetsCacheFile,TargetPath,TargetFramework,TargetFrameworks,RunAnalyzers,RunAnalyzersDuringLiveAnalysis", "-getItem:ProjectReference,Compile,AdditionalFiles,Analyzer,EditorConfigFiles,GlobalAnalyzerConfigFiles,Reference",
            "-p:Configuration=" + configuration, "-p:StrideSkipAutoPack=true" }) start.ArgumentList.Add(argument);
        if (framework.Length > 0) start.ArgumentList.Add("-p:TargetFramework=" + framework);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Could not evaluate script project watch paths.");
        async Task<string> ReadOutput(StreamReader reader)
        {
            var text = new StringBuilder();
            var exceeded = false;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (text.Length + line.Length < 32 * 1024 * 1024) text.AppendLine(line);
                else exceeded = true;
            }
            if (exceeded) throw new IOException("The evaluated project input inventory exceeds the supported output limit.");
            return text.ToString();
        }
        var stdout = ReadOutput(process.StandardOutput); var stderr = ReadOutput(process.StandardError);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("Script project watch-path evaluation timed out.");
        }
        var output = await stdout.ConfigureAwait(false); var errors = await stderr.ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(output + errors);
        var beginning = output.IndexOf('{');
        if (beginning < 0) throw new IOException("MSBuild returned no script project watch paths.");
        using var json = JsonDocument.Parse(output[beginning..]);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project };
        var directory = Path.GetDirectoryName(project)!;
        bool? run = null, live = null;
        var targetPolicies = new List<ScriptProjectWatchTarget>();
        if (json.RootElement.TryGetProperty("Properties", out var properties))
        {
            bool? Enabled(string name) => properties.TryGetProperty(name, out var value) && bool.TryParse(value.GetString(), out var enabled) ? enabled : null;
            run = Enabled("RunAnalyzers"); live = Enabled("RunAnalyzersDuringLiveAnalysis");
            if (properties.TryGetProperty("TargetPath", out var targetPath) && targetPath.GetString() is { Length: > 0 } outputPath)
                targetPolicies.Add(new(Path.GetFullPath(outputPath, directory), run, live));
            if (framework.Length == 0)
            {
                var targets = properties.TryGetProperty("TargetFrameworks", out var multiple) ? multiple.GetString() : null;
                if (string.IsNullOrWhiteSpace(targets) && properties.TryGetProperty("TargetFramework", out var single)) targets = single.GetString();
                var evaluations = new List<ScriptProjectWatchEvaluation>();
                foreach (var target in (targets ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
                {
                    var evaluated = await Evaluate(project, configuration, target, cancellation, visited).ConfigureAwait(false);
                    evaluations.Add(evaluated); paths.UnionWith(evaluated.Paths);
                }
                if (evaluations.Count > 0)
                {
                    targetPolicies.Clear(); targetPolicies.AddRange(evaluations.SelectMany(value => value.Targets ?? []));
                    run = evaluations.Select(value => value.RunAnalyzers).Distinct().Count() == 1 ? evaluations[0].RunAnalyzers : null;
                    live = evaluations.Select(value => value.RunAnalyzersDuringLiveAnalysis).Distinct().Count() == 1 ? evaluations[0].RunAnalyzersDuringLiveAnalysis : null;
                }
            }
            if (properties.TryGetProperty("MSBuildAllProjects", out var imports))
                foreach (var path in (imports.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) paths.Add(Path.GetFullPath(path, directory));
            foreach (var property in new[] { "ProjectAssetsFile", "ProjectAssetsCacheFile" })
                if (properties.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } path)
                    paths.Add(Path.GetFullPath(path, directory));
        }
        if (json.RootElement.TryGetProperty("Items", out var items))
            foreach (var type in items.EnumerateObject())
                foreach (var item in type.Value.EnumerateArray())
                {
                    var metadata = type.Name == "Reference" ? "HintPath" : "FullPath";
                    if (item.TryGetProperty(metadata, out var path) && path.GetString() is { Length: > 0 } value)
                    {
                        paths.Add(Path.GetFullPath(value, directory));
                        if (type.Name == "ProjectReference" && File.Exists(Path.GetFullPath(value, directory)))
                            paths.UnionWith((await Evaluate(Path.GetFullPath(value, directory), configuration, "", cancellation, visited).ConfigureAwait(false)).Paths);
                    }
                }
        return new(paths.ToArray(), run, live, targetPolicies.ToArray());
    }
}
