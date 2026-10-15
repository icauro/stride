using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;

namespace Stride.Editor.ImGui;

/// <summary>Runs the existing project/asset compiler outside the UI and engine process.</summary>
internal static class CompiledProjectBuild
{
    public static string Build(string packagePath, CancellationToken cancellation = default, Action<ProjectLoadState>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var project = string.Equals(Path.GetExtension(packagePath), ".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(packagePath) : Path.ChangeExtension(packagePath, ".csproj");
        if (!File.Exists(project)) throw new FileNotFoundException("Compiled previews require the package's project file.", project);
        var compiler = AssetCompilerLocator.GetExecutable();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packagePath)))[..24];
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "build", identity);
        var output = Path.Combine(cache, "runs", Guid.NewGuid().ToString("N"));
        OwnedProjectBuildOutput.EnsureFailedOutputAdmission();
        try
        {
        Directory.CreateDirectory(output);
        var api = EditorGraphicsBackend.SelectedApi;
        var prepared = PreparedCompiledFixture.Identify(project, compiler, output);
        if (PreparedCompiledFixture.TryCopy(prepared, output, cancellation))
        {
            progress?.Invoke(new("Prepared fixture", "Using verified compiled fixture copy", output));
            return output;
        }
        Run("dotnet", ["build", project, "--disable-build-servers", "-m:1", "-c", "Debug", "-p:StrideGraphicsApi=" + api, "-p:StrideGraphicsApis=" + api, "-p:StrideSkipAutoPack=true", "-v", "quiet"], output, "project", cancellation, progress, "project:" + project);
        var manifest = Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(project)!, "obj"), "*.sdbuild", SearchOption.AllDirectories)
            .Where(path =>
            {
                var data = AssetFileSerializer.Load<AssetBuildManifest>(path).Asset;
                return data.ProjectFile != null && string.Equals(AssetBuildManifest.ResolvePath(path, data.ProjectFile), project, StringComparison.OrdinalIgnoreCase);
            }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new FileNotFoundException("The project build did not produce an asset build manifest.");
        Run(compiler, ["build", manifest, "--platform=Windows", "--project-configuration=Debug", "--compile-property:StrideGraphicsApi=" + api, "--compile-property:StrideGraphicsApis=" + api, "--compile-property:StrideSkipAutoPack=true", "--compile-property:StrideEditorSkipPackageUpgrade=true", "--output-path=" + output, "--build-path=" + Path.Combine(cache, "objects"), "--threads=4"], output, "assets", cancellation, progress, "assets:" + project);
        if (!File.Exists(Path.Combine(output, "db", "bundles", "default.bundle")))
            throw new FileNotFoundException("The asset compiler produced no default content bundle.");
        if (!NativeCompilerProcessRetirement.TryDrainForOutput(output))
            throw new IOException("Native compiler retirement is pending.");
        PreparedCompiledFixture.Publish(prepared, output);
        return output;
        }
        catch (Exception error)
        {
            OwnedProjectBuildOutput.RetireFailedOutput(output);
            // Preserve the original exception type/stack, while keeping the
            // actual archived native diagnostic location available to callers.
            try { error.Data["StrideCompilerArchivedLogs"] = OwnedProjectBuildOutput.FailedOutputLogDirectory(output); }
            catch { /* Diagnostic annotation cannot replace the real producer failure. */ }
            throw;
        }
    }

    internal static void Run(string executable, string[] arguments, string logDirectory, string name, CancellationToken cancellation, Action<ProjectLoadState>? progress, string? diagnosticSource = null)
    {
        var run = CompilerRunIdentity.Start(diagnosticSource ?? name + ":" + executable);
        var phase = name == "project" ? "Building project" : "Compiling assets";
        var diagnosticPath = Path.Combine(logDirectory, run.Id.ToString("N") + ".asset-diagnostics.jsonl");
        progress?.Invoke(new(phase, "Starting compiler...", logDirectory, CompilerRun: run, CompilerEvent: CompilerRunEvent.Started));
        var outcome = CompilerRunEvent.Failed;
        var invoked = false;
        var collected = false;
        void CollectDiagnostics()
        {
            if (collected) return;
            collected = true;
            foreach (var diagnostic in NativeAssetDiagnostic.Read(diagnosticPath, outcome == CompilerRunEvent.Cancelled))
                progress?.Invoke(new(phase, diagnostic.Text, logDirectory, CompilerRun: run, AssetDiagnostic: diagnostic));
        }
        try
        {
            RunCore(executable, arguments, logDirectory, name, cancellation, state =>
            {
                if (state.ProcessId != null) invoked = true;
                progress?.Invoke(state with { CompilerRun = run });
            }, diagnosticPath);
            CollectDiagnostics(); outcome = CompilerRunEvent.Succeeded;
        }
        catch (OperationCanceledException) { outcome = CompilerRunEvent.Cancelled; throw; }
        catch { if (!invoked) outcome = CompilerRunEvent.FailedToStart; throw; }
        finally
        {
            try { CollectDiagnostics(); }
            finally { progress?.Invoke(new(phase, "Build finished", logDirectory, CompilerRun: run, CompilerEvent: outcome)); }
        }
    }
    private static void RunCore(string executable, string[] arguments, string logDirectory, string name, CancellationToken cancellation, Action<ProjectLoadState>? progress, string diagnosticPath)
    {
        cancellation.ThrowIfCancellationRequested();
        logDirectory = Path.GetFullPath(logDirectory);
        NativeCompilerProcessRetirement.EnsureAdmission();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = logDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["STRIDE_GRAPHICS_API"] = EditorGraphicsBackend.SelectedApi;
        if (name == "project") start.Environment.Remove("STRIDE_EDITOR_ASSET_DIAGNOSTICS");
        else start.Environment["STRIDE_EDITOR_ASSET_DIAGNOSTICS"] = diagnosticPath;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable);
        var lifetime = NativeCompilerProcessRetirement.Register(process, logDirectory);
        try
        {
        var phase = name == "project" ? "Building project" : "Compiling assets";
        progress?.Invoke(new ProjectLoadState(phase, "Starting compiler...", logDirectory, process.Id));
        var log = new StreamWriter(Path.Combine(logDirectory, name + ".log"));
        lifetime.RetainLog(log);
        var nativeMigrationRequired = 0;
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (log) { log.WriteLine(line); log.Flush(); }
                if (line.Contains(NativeMigrationRequiredException.CompilerMarker, StringComparison.Ordinal)) Interlocked.Exchange(ref nativeMigrationRequired, 1);
                progress?.Invoke(new ProjectLoadState(phase, line.Length > 300 ? line[..300] + "…" : line, logDirectory, process.Id, line,
                    BuildDiagnostic.Parse(line, start.WorkingDirectory)));
            }
        }
        var stdout = Read(process.StandardOutput); lifetime.RetainReader(stdout);
        var stderr = Read(process.StandardError); lifetime.RetainReader(stderr);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            // The finally-owned real process/readers settle even if kill or log
            // closure fails; their owner remains retained until retry succeeds.
            lifetime.BeginRetirement();
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"{name} build timed out. Logs: {logDirectory}");
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        cancellation.ThrowIfCancellationRequested();
        if (Volatile.Read(ref nativeMigrationRequired) != 0) throw new NativeMigrationRequiredException();
        progress?.Invoke(new ProjectLoadState(phase, "Build finished", logDirectory, null));
        if (process.ExitCode != 0) throw new InvalidOperationException($"{name} build failed ({process.ExitCode}). Logs: {logDirectory}");
        }
        finally { lifetime.BeginRetirement(); }
    }
}
