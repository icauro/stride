using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NuGet.Frameworks;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record RuntimeAssemblyOwner(PackageLoadedAssembly Owner, System.Reflection.Assembly? Assembly, string Path, string ProjectPath);
    private sealed record ScriptRuntimeCompilation(string ProjectPath, string Framework, ScriptRuntimeArtifacts Artifacts,
        AuthoredProjectSnapshot[] Snapshots, RuntimeAssemblyOwner[] Owners);

    private sealed class ScriptRuntimeArtifacts(AuthoredProjectCompilationResolution resolution, Action<Exception> report) : IAssemblyArtifactLifetime, IDisposable
    {
        private readonly object gate = new();
        private int references = 1;
        private bool initialReleased;
        public AuthoredProjectCompilationResolution Resolution { get; } = resolution;
        public bool Owns(string path) => Path.GetFullPath(path).StartsWith(Resolution.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (AuthoredProjectCompilationResolution Resolution, Action<Exception> Report)> pending = new(StringComparer.OrdinalIgnoreCase);
        public IDisposable Acquire()
        {
            lock (gate)
            {
                if (references == 0) throw new ObjectDisposedException(nameof(ScriptRuntimeArtifacts));
                references++;
                return new Lease(this);
            }
        }
        public void Dispose()
        {
            lock (gate) { if (initialReleased) return; initialReleased = true; }
            Release();
        }
        private void Release()
        {
            lock (gate)
            {
                if (--references != 0) return;
                try { Resolution.Dispose(); }
                catch (Exception error) { pending.TryAdd(Resolution.Directory, (Resolution, report)); report(error); }
            }
        }
        public static void RetryCleanup()
        {
            foreach (var pair in pending.ToArray())
                try { pair.Value.Resolution.Dispose(); pending.TryRemove(pair.Key, out _); }
                catch (Exception error) { pair.Value.Report(error); }
        }
        private sealed class Lease(ScriptRuntimeArtifacts owner) : IDisposable
        {
            private ScriptRuntimeArtifacts? retained = owner;
            public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
        }
    }
    private sealed class ScriptRuntimeArtifactGroup(ScriptRuntimeArtifacts[] artifacts) : IAssemblyArtifactLifetime
    {
        public bool Owns(string path) => artifacts.Any(artifact => artifact.Owns(path));
        public IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try { foreach (var source in artifacts) leases.Add(source.Acquire()); return new Leases(leases); }
            catch { foreach (var lease in leases) lease.Dispose(); throw; }
        }
        private sealed class Leases(List<IDisposable> leases) : IDisposable
        {
            public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
        }
    }
    private RuntimeAssemblyOwner[] CaptureRuntimeAssemblyOwners() => workspace!.Session.Packages.Where(package => !package.IsReadOnly)
        .SelectMany(package => package.LoadedAssemblies).Where(owner => owner.ProjectReference?.Location != null)
        .Select(owner => new RuntimeAssemblyOwner(owner, owner.Assembly, owner.Path, Path.GetFullPath(owner.ProjectReference.Location.ToOSPath()))).ToArray();

    private string ResolveAuthoredRuntimeFramework(string projectPath, RuntimeAssemblyOwner[] owners)
    {
        var project = workspace!.Projects.Single(project => SamePath(project.FullPath.ToOSPath(), projectPath));
        var contexts = GetProjectBuildSettingsDocument(project, sessionHistory!).EvaluateDependencyContexts("Debug");
        if (liveExecutableBuild is { } executable && SamePath(executable.ProjectPath, projectPath))
            return contexts.Single(context => context.Framework == executable.Framework).Framework;
        if (contexts.Length == 1) return contexts[0].Framework;
        var assemblies = owners.Where(owner => SamePath(owner.ProjectPath, projectPath)).Select(owner => owner.Assembly).Where(assembly => assembly != null).Distinct().ToArray();
        var matches = contexts.Where(context => assemblies.Any(assembly =>
        {
            var attribute = assembly!.GetCustomAttribute<TargetFrameworkAttribute>();
            if (attribute == null) return false;
            var loaded = NuGetFramework.ParseFrameworkName(attribute.FrameworkName, DefaultFrameworkNameProvider.Instance);
            var evaluated = NuGetFramework.ParseFolder(context.Framework);
            if (loaded.Framework != evaluated.Framework || loaded.Version != evaluated.Version || loaded.Profile != evaluated.Profile) return false;
            var platform = assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName ?? "";
            return evaluated.Platform.Length == 0 ? platform.Length == 0 : platform.StartsWith(evaluated.Platform, StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(platform[evaluated.Platform.Length..], out var version) && version == evaluated.PlatformVersion;
        })).ToArray();
        return matches.Length == 1 ? matches[0].Framework : throw new InvalidOperationException("The retained project has no unique loaded target framework.");
    }
    private async Task<ScriptRuntimeCompilation> CompileAuthoredRuntimeProject(string projectPath, string framework,
        AuthoredProjectSnapshot[] snapshots, RuntimeAssemblyOwner[] owners, CancellationToken cancellation)
    {
        var log = new LoggerResult();
        try
        {
            var resolution = await AuthoredProjectCompilationResolver.Resolve(snapshots, projectPath, "Debug", framework, log, cancellation, compile: true).ConfigureAwait(false);
            var artifacts = new ScriptRuntimeArtifacts(resolution, error => outputLog.Append("Build", LogMessageType.Error, "Reload assemblies", "Compiler artifact cleanup failed: " + error));
            return new(projectPath, framework, artifacts, snapshots, owners);
        }
        finally { foreach (var message in log.Messages) outputLog.Append("Build", message.Type, "Reload assemblies", message.ToString()); }
    }
    private void ApplyRuntimeCompilations(ScriptRuntimeCompilation[] builds)
    {
        if (builds.Length == 0) return;
        var outputs = new Dictionary<PackageLoadedAssembly, string>();
        var executableOutputs = new List<(ScriptRuntimeCompilation Build, string Path)>();
        foreach (var build in builds)
        {
            foreach (var expected in build.Snapshots)
            {
                var owner = authoring!.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
                    .SingleOrDefault(document => SamePath(document.Path, expected.Path)) ?? throw new InvalidOperationException("An authored compilation owner is no longer retained.");
                if (owner.CaptureProjectSnapshot().Xml != expected.Xml) throw new InvalidOperationException("The authored project changed during compilation.");
            }
            foreach (var input in build.Artifacts.Resolution.Inputs)
            {
                using var file = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!SHA256.HashData(file).AsSpan().SequenceEqual(input.Hash)) throw new InvalidOperationException("A compilation input changed before assembly reload.");
            }
            foreach (var project in build.Artifacts.Resolution.Projects)
            {
                if (project.AssemblyPath == null) continue;
                if (liveExecutableBuild is { } executable && SamePath(executable.ProjectPath, project.ProjectPath)
                    && executable.Framework == project.Framework)
                    executableOutputs.Add((build, project.AssemblyPath));
                foreach (var owner in build.Owners.Where(owner => SamePath(owner.ProjectPath, project.ProjectPath)))
                {
                    if (!ReferenceEquals(owner.Owner.Assembly, owner.Assembly) || owner.Owner.Path != owner.Path)
                        throw new InvalidOperationException("The actual loaded assembly owner changed during compilation.");
                    // Native nearest-framework graph selection determines this
                    // output; source project identity is never inferred by name.
                    if (outputs.TryGetValue(owner.Owner, out var previous) && !SamePath(previous, project.AssemblyPath))
                    {
                        var previousProject = builds.SelectMany(candidate => candidate.Artifacts.Resolution.Projects).Single(candidate => candidate.AssemblyPath == previous);
                        if (previousProject.Framework != project.Framework) throw new InvalidOperationException("A loaded project resolved to different runtime target frameworks.");
                    }
                    outputs[owner.Owner] = project.AssemblyPath;
                }
            }
        }
        if (outputs.Count == 0 && executableOutputs.Count == 0) throw new InvalidOperationException("No compiled target has an actual loaded package assembly or live executable owner.");
        var executableSnapshots = executableOutputs.Select(output => (output.Build,
            Assembly: LiveScriptSession.Capture(liveExecutableBuild!.ProjectPath, output.Path))).ToArray();
        if (executableSnapshots.Select(output => output.Assembly.ModuleId).Distinct().Count() > 1)
            throw new InvalidOperationException("The live executable resolved to conflicting runtime generations.");
        var log = new LoggerResult();
        try
        {
            if (outputs.Count != 0) ReloadScriptAssemblies(outputs, log, new ScriptRuntimeArtifactGroup(builds.Select(build => build.Artifacts).ToArray()));
            foreach (var output in executableSnapshots)
            {
                liveScriptSession!.RetainArtifact(output.Build.Artifacts.Acquire());
                liveExecutableAssembly = output.Assembly;
            }
            foreach (var output in outputs) { assemblyOutputPaths[output.Key] = output.Value; observedAssemblyHashes[output.Key] = ReadAssemblyHash(output.Value); failedAssemblyReloads.Remove(output.Key); }
            WatchAssemblyOutputDirectories();
            outputLog.Append("Build", LogMessageType.Info, "Reload assemblies", "Assemblies reloaded");
        }
        finally { foreach (var message in log.Messages) outputLog.Append("Build", message.Type, "Reload assemblies", message.ToString()); }
    }
}
