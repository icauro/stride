using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using Stride.Core;

namespace Stride.ConnectionRouter;

internal static class EditorServiceLocator
{
    internal static string FindBundledEffectCompiler(string packageName, string packageVersion, string process)
    {
        // Other requested Stride versions/custom services continue through PackageStore.
        if (packageName != "Stride.EffectCompilerServer" || packageVersion != StrideVersion.NuGetVersion
            || process != (OperatingSystem.IsWindows() ? "Stride.EffectCompilerServer.exe" : "Stride.EffectCompilerServer")) return null;
        var root = AppContext.BaseDirectory;
        var staged = Path.Combine(root, "tools", "effect-compiler-server");
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) == "router"
            && Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))) == "tools")
            staged = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)), "effect-compiler-server");
        var executable = Path.Combine(staged, process);
        var assembly = Path.Combine(staged, "Stride.EffectCompilerServer.dll");
        if (!File.Exists(executable) || !File.Exists(assembly) || !File.Exists(Path.Combine(staged, "Stride.EffectCompilerServer.runtimeconfig.json"))
            || !File.Exists(Path.Combine(staged, "Stride.EffectCompilerServer.deps.json"))) return null;
        var bundledVersion = FileVersionInfo.GetVersionInfo(assembly).ProductVersion?.Split('+')[0];
        return bundledVersion == packageVersion && AssemblyName.GetAssemblyName(assembly).Version == typeof(Router).Assembly.GetName().Version ? executable : null;
    }
}
