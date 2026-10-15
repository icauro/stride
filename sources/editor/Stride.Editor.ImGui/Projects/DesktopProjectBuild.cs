using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed record DesktopProjectMetadata(string[] Configurations, string[] Frameworks, bool Executable);
internal sealed record DesktopProjectBuildResult(string ProjectPath, string Configuration, string Framework, string TargetPath, bool Executable, bool UseAppHost,
    string GraphicsApi = "");

internal static partial class DesktopProjectBuild
{
    public static async Task<string> ResolveEditorBuildFramework(string project, IReadOnlyCollection<string> actualTargetPaths, CancellationToken cancellation)
    {
        var metadata = await ReadMetadata(project, cancellation);
        if (metadata.Frameworks.Length <= 1) return metadata.Frameworks.FirstOrDefault() ?? "";
        var matches = new List<string>();
        foreach (var framework in metadata.Frameworks)
        {
            var properties = await Evaluate(project, ["-p:Configuration=Debug", "-p:TargetFramework=" + framework], "TargetPath", cancellation);
            var target = properties.GetValueOrDefault("TargetPath");
            if (!string.IsNullOrWhiteSpace(target) && actualTargetPaths.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(target, Path.GetDirectoryName(project)!), StringComparison.OrdinalIgnoreCase)))
                matches.Add(framework);
        }
        return matches.Count == 1 ? matches[0] : throw new InvalidOperationException("The loaded editor assembly has no unique evaluated project target framework.");
    }
    public static async Task<DesktopProjectMetadata> ReadMetadata(string project, CancellationToken cancellation)
    {
        var properties = await Evaluate(project, [], "Configurations,Configuration,TargetFrameworks,TargetFramework,OutputType", cancellation);
        var configurations = Split(properties.GetValueOrDefault("Configurations"));
        if (configurations.Length == 0) configurations = Split(properties.GetValueOrDefault("Configuration"));
        var frameworks = Split(properties.GetValueOrDefault("TargetFrameworks"));
        if (frameworks.Length == 0) frameworks = Split(properties.GetValueOrDefault("TargetFramework"));
        if (configurations.Length == 0) throw new InvalidOperationException("The project has no build configuration.");
        return new(configurations, frameworks, IsExecutable(properties.GetValueOrDefault("OutputType")));
    }

    public static async Task<DesktopProjectBuildResult> Build(string project, string configuration, string framework, string solution,
        CancellationToken cancellation, Action<ProjectLoadState> progress, string platform = "")
    {
        var properties = new List<string> { "-p:Configuration=" + configuration, "-p:StrideSkipAutoPack=true", "-p:StrideBuildEngineLogVerbose=true" };
        if (!string.IsNullOrWhiteSpace(platform))
        { properties.Add("-p:Platform=" + platform); properties.Add("-p:SolutionPlatform=" + platform); }
        if (framework.Length > 0) properties.Add("-p:TargetFramework=" + framework);
        if (Path.GetExtension(solution).Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            properties.Add("-p:SolutionPath=" + solution);
            properties.Add("-p:SolutionDir=" + Path.GetDirectoryName(solution) + Path.DirectorySeparatorChar);
        }
        var arguments = new List<string> { "build", project, "--disable-build-servers", "-v", "minimal" };
        arguments.AddRange(properties);
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "BuildLogs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
        progress(new("Building project", logPath, OutputLine: "Build log: " + logPath));
        using (var log = new StreamWriter(logPath, false, new UTF8Encoding(false)))
        {
            log.WriteLine(project + " (" + configuration + ", " + framework + ")");
            await Run(arguments, Path.GetDirectoryName(project)!, cancellation,
                (line, id) =>
                {
                    lock (log) log.WriteLine(line);
                    progress(new("Building project", line, ProcessId: id, OutputLine: line,
                        Diagnostic: BuildDiagnostic.Parse(line, Path.GetDirectoryName(project)!)));
                }, false);
        }
        return await ReadBuiltOutput(project, configuration, framework, properties, cancellation).ConfigureAwait(false);
    }
    private static async Task<DesktopProjectBuildResult> ReadBuiltOutput(string project, string configuration, string framework,
        IEnumerable<string> properties, CancellationToken cancellation)
    {
        var output = await Evaluate(project, properties, "TargetPath,OutputType,UseAppHost,StrideGraphicsApi", cancellation);
        var target = output.GetValueOrDefault("TargetPath") ?? "";
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("MSBuild returned no target output path.");
        if (!Path.IsPathFullyQualified(target)) target = Path.GetFullPath(target, Path.GetDirectoryName(project)!);
        if (!File.Exists(target)) throw new FileNotFoundException("The project build did not produce its target output.", target);
        var type = output.GetValueOrDefault("OutputType");
        return new(project, configuration, framework, target, IsExecutable(type),
            bool.TryParse(output.GetValueOrDefault("UseAppHost"), out var appHost) && appHost, output.GetValueOrDefault("StrideGraphicsApi") ?? "");
    }

    public static int Start(DesktopProjectBuildResult result)
    {
        if (!result.Executable) throw new InvalidOperationException("The selected project is not executable.");
        var target = result.TargetPath;
        if (!File.Exists(target)) throw new FileNotFoundException("The project output is no longer available.", target);
        var host = Path.ChangeExtension(target, ".exe");
        ProcessStartInfo start;
        if (Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) start = new(target);
        else if (result.UseAppHost)
        {
            if (!File.Exists(host)) throw new FileNotFoundException("The project launcher is missing.", host);
            start = new(host);
        }
        else
        {
            start = new("dotnet"); start.ArgumentList.Add(target);
        }
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.WorkingDirectory = Path.GetDirectoryName(target)!;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The project could not be started.");
        return process.Id;
    }

    private static string[] Split(string? value) => (value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool IsExecutable(string? outputType) => string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, string>> Evaluate(string project, IEnumerable<string> properties, string names, CancellationToken cancellation)
    {
        var arguments = new List<string> { "msbuild", project, "-nologo", "-getProperty:" + names };
        arguments.AddRange(properties);
        var text = await Run(arguments, Path.GetDirectoryName(project)!, cancellation, null, true);
        var beginning = text.IndexOf('{');
        if (beginning < 0) throw new InvalidOperationException("MSBuild returned no project properties: " + text);
        using var json = JsonDocument.Parse(text[beginning..]);
        return json.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> Run(IEnumerable<string> arguments, string directory, CancellationToken cancellation, Action<string, int>? output, bool collect,
        string executable = "dotnet")
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        var text = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        async Task Read(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (collect) lock (text) { if (text.Length < 4 * 1024 * 1024) text.AppendLine(line); }
                    output?.Invoke(line, process.Id);
                }
            }
            catch { timeout.Cancel(); throw; }
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("Project build timed out.");
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("Project build failed (" + process.ExitCode + ")." + (collect ? Environment.NewLine + text : ""));
        return text.ToString();
    }
}
