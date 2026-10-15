using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal static partial class DesktopProjectBuild
{
    // The WPF build path installs the real SDK package on the first connected device.
    public static async Task BuildAndRunAndroid(string project, string configuration, string framework, string solution,
        CancellationToken cancellation, Action<ProjectLoadState> progress)
    {
        _ = await BuildAndInstallAndroid(project, configuration, framework, solution, cancellation, progress, launch: true).ConfigureAwait(false);
    }

    public static async Task<DesktopProjectBuildResult> BuildAndInstallAndroid(string project, string configuration, string framework, string solution,
        CancellationToken cancellation, Action<ProjectLoadState> progress, bool launch = false)
    {
        var devices = await Task.Run(Stride.AndroidDeviceEnumerator.ListAndroidDevices, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (devices.Length == 0) throw new InvalidOperationException("No Android device found for execution.");
        var serial = devices[0].Serial;
        var arguments = new List<string> { "msbuild", project, "-nologo", "-nodeReuse:false", "-verbosity:minimal",
            "-target:GetAndroidPackage;Install", "-getTargetResult:GetAndroidPackage", "-p:Configuration=" + configuration,
            "-p:Platform=Android", "-p:SolutionPlatform=Android", "-p:AdbTarget=-s " + serial,
            "-p:StrideSkipAutoPack=true", "-p:StrideBuildEngineLogVerbose=true" };
        if (!string.IsNullOrEmpty(framework)) arguments.Add("-p:TargetFramework=" + framework);
        if (Path.GetExtension(solution).Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-p:SolutionPath=" + solution);
            arguments.Add("-p:SolutionDir=" + Path.GetDirectoryName(solution) + Path.DirectorySeparatorChar);
        }
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "BuildLogs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
        progress(new("Building project", logPath, OutputLine: "Build log: " + logPath));
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false));
        void Output(string line, int id)
        {
            lock (log) log.WriteLine(line);
            progress(new("Building project", line, ProcessId: id, OutputLine: line,
                Diagnostic: BuildDiagnostic.Parse(line, Path.GetDirectoryName(project)!)));
        }
        var text = await Run(arguments, Path.GetDirectoryName(project)!, cancellation, Output, true).ConfigureAwait(false);
        if (launch)
        {
        var adb = Stride.AndroidDeviceEnumerator.GetAdbPath()
            ?? throw new InvalidOperationException("Android tool \"adb\" couldn't found (no running process, in registry or on the PATH). Please add it to your PATH.");
        string? package = null;
        // Native MSBuild may emit logger lines before its final structured target result.
        for (var index = text.IndexOf('{'); index >= 0; index = text.IndexOf('{', index + 1))
        {
            try
            {
                using var result = JsonDocument.Parse(text[index..]);
                var target = result.RootElement.GetProperty("TargetResults").GetProperty("GetAndroidPackage");
                if (target.GetProperty("Result").GetString() != "Success") break;
                var items = target.GetProperty("Items");
                if (items.GetArrayLength() != 1) throw new InvalidOperationException("Couldn't find Android package name for " + Path.GetFileNameWithoutExtension(project) + ".");
                package = items[0].GetProperty("Identity").GetString(); break;
            }
            catch (JsonException) { }
        }
        if (string.IsNullOrWhiteSpace(package)) throw new InvalidOperationException("Couldn't find Android package name for " + Path.GetFileNameWithoutExtension(project) + ".");
        await Run(["-s", serial, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"],
            Path.GetDirectoryName(project)!, cancellation, Output, false, adb).ConfigureAwait(false);
        }
        progress(new("Deployment", project, OutputLine: "Deployment of " + Path.GetFileNameWithoutExtension(project) + " successful."));
        return await ReadBuiltOutput(project, configuration, framework,
            arguments.Where(argument => argument.StartsWith("-p:", StringComparison.Ordinal)), cancellation).ConfigureAwait(false);
    }
}
