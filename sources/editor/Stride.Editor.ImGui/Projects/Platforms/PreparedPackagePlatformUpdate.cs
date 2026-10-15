using System.Security.Cryptography;
using Stride.Assets.Templates;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

/// <summary>Native updateOnly output and exact source receipts prepared without changing the live workspace.</summary>
internal sealed class PreparedPackagePlatformUpdate : IDisposable
{
    internal sealed record FileChange(string RelativePath, byte[]? Before, OwnedFileIdentity? BeforeIdentity, byte[]? After);
    internal readonly NativeMigrationMirror Mirror;
    internal readonly FileChange[] Changes;
    internal readonly (string RelativePath, OwnedDirectoryIdentity Identity)[] BeforeDirectories;
    internal readonly string[] AfterDirectories;
    internal readonly (PlatformType Platform, string Path, Guid? RemovedId)[] Projects;
    internal readonly ILogMessage[] Messages;
    private PreparedPackagePlatformUpdate(NativeMigrationMirror mirror, FileChange[] changes,
        (PlatformType, string, Guid?)[] projects, ILogMessage[] messages,
        (string, OwnedDirectoryIdentity)[] beforeDirectories, string[] afterDirectories)
    { Mirror = mirror; Changes = changes; Projects = projects; Messages = messages; BeforeDirectories = beforeDirectories; AfterDirectories = afterDirectories; }
    internal static PreparedPackagePlatformUpdate Prepare(PackagePlatformUpdateSnapshot snapshot, CancellationToken cancellation)
    {
        var mirror = NativeMigrationMirror.Create(snapshot.LibraryPath, cancellation, snapshot.Root, deferProjectValidation: true);
        PackageSession? session = null;
        try
        {
            foreach (var project in snapshot.Projects) mirror.StageProjectRoot(project.ProjectPath, cancellation);
            string Stage(string source) => mirror.ProjectInputPath(source);
            foreach (var input in snapshot.Inputs)
            {
                cancellation.ThrowIfCancellationRequested(); var path = Stage(input.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var hash = File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null;
                WindowsOwnedAssetFileWriter.Write(path, hash, input.Bytes);
            }
            // Strip only the captured genuinely removed incoming declarations in
            // private bytes, through the same native producer as canonical project
            // history. This lets native loading/compilation evaluate the exact
            // post-removal graph; live persistence is still the guarded actor journal.
            foreach (var group in snapshot.RemovingDependencies.GroupBy(dependency => dependency.ProjectPath, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested(); var staged = Stage(group.Key);
                var before = File.ReadAllBytes(staged);
                using var properties = new AuthoredProjectProperties(before, staged);
                foreach (var dependency in group)
                {
                    PackageVersionRange? range = null;
                    if (dependency.VersionRange != null && !PackageVersionRange.TryParse(dependency.VersionRange, out range))
                        throw new InvalidOperationException("A captured native dependency version is invalid.");
                    properties.SetDependencyReference(new DependencyRange(dependency.Name, range!, dependency.Type)
                        { MSBuildProject = dependency.MSBuildProject }, null, group.Key);
                }
                WindowsOwnedAssetFileWriter.Write(staged, SHA256.HashData(before), properties.Serialize());
            }
            foreach (var input in snapshot.Projects)
            {
                cancellation.ThrowIfCancellationRequested(); var staged = Stage(input.ProjectPath);
                var before = File.ReadAllBytes(staged);
                using var properties = new AuthoredProjectProperties(before, input.ProjectPath);
                properties.RebaseProjectReferences(path => mirror.StageProjectReference(path, cancellation), path => mirror.StageProjectImport(path, input.ProjectPath, cancellation),
                    (path, bytes) => mirror.PublishProjectImport(path, input.ProjectPath, bytes, cancellation),
                    path => mirror.StageProjectResource(path, cancellation), stageTaskAssembly: path => mirror.StageTaskAssembly(path, cancellation));
                WindowsOwnedAssetFileWriter.Write(staged, SHA256.HashData(before), properties.Serialize());
            }
            mirror.RebaseCopiedSolutions(cancellation);
            var log = new LoggerResult(); session = new();
            foreach (var input in snapshot.Projects)
            {
                cancellation.ThrowIfCancellationRequested();
                var loaded = Package.LoadProject(log, Stage(input.ProjectPath));
                if (loaded is not SolutionProject source) throw new InvalidOperationException("The native platform input is not a project.");
                // The actual authored project ID/type/platform are captured from the
                // live native model; raw loading does not evaluate or compile it.
                var project = new SolutionProject(source.Package, input.Id, Stage(input.ProjectPath))
                { Type = input.Type, Platform = input.Platform, IsImplicitProject = input.IsImplicitProject };
                project.ApplyAuthoredDirectDependencies(source.DirectDependencies.ToArray());
                session.Projects.Add(project);
            }
            if (log.HasErrors) throw new InvalidOperationException(log.ToText());
            var selectedPackage = session.Packages.Single(package => SamePath(package.FullPath.ToOSPath(), Stage(snapshot.SelectedPackagePath)));
            var parameters = new PackageTemplateGeneratorParameters
            {
                Name = snapshot.Name, Package = selectedPackage, Description = UpdatePlatformsGenerator.CreateDescription(),
                OutputDirectory = selectedPackage.FullPath.GetFullDirectory(), Logger = log, Unattended = true,
            };
            UpdatePlatformsGenerator.SetPlatforms(parameters, snapshot.Selected, snapshot.Regenerate);
            var plan = UpdatePlatformsGenerator.CreatePlan(parameters) ?? throw new InvalidOperationException("The native platform update could not resolve its game library.");
            if (!SamePath(plan.GameLibrary.FullPath.ToOSPath(), Stage(snapshot.LibraryPath)))
                throw new InvalidOperationException("The native platform update resolved a different game library.");
            if (!plan.ProjectsToRemove.Where(removal => removal.Project != null).Select(removal => removal.Project!.Id)
                    .SequenceEqual(snapshot.ExpectedRemovedProjects)
                || !plan.PlatformsToInstantiate.SequenceEqual(snapshot.ExpectedInstantiation))
                throw new InvalidOperationException("The private native platform plan differs from the captured session.");
            var outputDirectories = plan.ProjectsToRemove.Select(removal => removal.Directory.ToOSPath())
                .Concat(plan.PlatformsToInstantiate.Select(type => Path.Combine(plan.SessionDirectory.ToOSPath(), plan.BaseName + "." + type)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var directory in outputDirectories)
                if (!NativeMigrationMirror.Inside(mirror.StageRoot, directory) || SamePath(directory, mirror.StageRoot))
                    throw new IOException("The native platform output is outside its private stage.");
            // Remove only authenticated private copies, never Run against the live
            // package. Exact original bytes remain in captured file receipts below.
            var originals = new Dictionary<string, (byte[] Bytes, OwnedFileIdentity Identity)>(StringComparer.OrdinalIgnoreCase);
            var originalDirectories = new Dictionary<string, OwnedDirectoryIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var stagedDirectory in outputDirectories)
            {
                var relative = Path.GetRelativePath(mirror.StageRoot, stagedDirectory);
                var original = Path.Combine(mirror.OriginalRoot, relative);
                if (Directory.Exists(original) && !plan.ProjectsToRemove.Any(removal => SamePath(removal.Directory.ToOSPath(), stagedDirectory)))
                    throw new IOException("The platform output directory is already occupied: " + original);
                if (Directory.Exists(original)) CaptureOriginalTree(original, mirror, originals, originalDirectories, cancellation);
            }
            foreach (var removal in plan.ProjectsToRemove)
            {
                cancellation.ThrowIfCancellationRequested(); RemovePrivateTree(removal.Directory.ToOSPath(), mirror.StageRoot);
            }
            cancellation.ThrowIfCancellationRequested();
            if (!UpdatePlatformsGenerator.GeneratePlatformFilesOnly(parameters, plan) || log.HasErrors)
                throw new InvalidOperationException(log.ToText());
            cancellation.ThrowIfCancellationRequested();
            var generatedProjects = plan.PlatformsToInstantiate.Select(type => Path.Combine(plan.SessionDirectory.ToOSPath(),
                plan.BaseName + "." + type, plan.BaseName + "." + type + ".csproj")).ToArray();
            if (generatedProjects.Any(path => !File.Exists(path))) throw new IOException("The native platform template did not produce every selected project.");
            mirror.ValidateProjectInputs(snapshot.Projects.Select(project => Stage(project.ProjectPath)).Where(File.Exists)
                .Concat(generatedProjects).Distinct(StringComparer.OrdinalIgnoreCase));
            mirror.ValidateExternalProjectInputs();
            var after = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var afterDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in outputDirectories.Where(Directory.Exists))
                CaptureFiles(directory, mirror.StageRoot, after, afterDirectories, cancellation);
            var changes = originals.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Select(relative =>
            {
                var before = originals.GetValueOrDefault(relative); var next = after.GetValueOrDefault(relative);
                return new FileChange(relative, before.Bytes, before.Bytes == null ? null : before.Identity, next);
            }).ToArray();
            var projects = plan.PlatformsToInstantiate.Select(type =>
                (type, Path.Combine(mirror.OriginalRoot, Path.GetRelativePath(mirror.StageRoot,
                    Path.Combine(plan.SessionDirectory.ToOSPath(), plan.BaseName + "." + type, plan.BaseName + "." + type + ".csproj"))),
                    (Guid?)null)).Concat(plan.ProjectsToRemove.Select(removal =>
                    (removal.Platform, removal.Project == null ? "" : mirror.OriginalPackagePath(removal.Project.FullPath.ToOSPath()), (Guid?)removal.Project?.Id))).ToArray();
            // Raw staging owns no runtime assembly/content, but its real native
            // session still retires before this output becomes available.
            session.Dispose(); session = null;
            return new(mirror, changes, projects, log.Messages.ToArray(), originalDirectories.Select(pair => (pair.Key, pair.Value)).ToArray(), afterDirectories.ToArray());
        }
        catch
        {
            var cleanup = new FailedStage(session, mirror);
            EditorBackgroundJobRetirement.Retire(Task.FromResult(cleanup), null, owner => owner.Dispose(), "Platform update preparation");
            throw;
        }
    }
    private sealed class FailedStage(PackageSession? session, NativeMigrationMirror mirror) : IDisposable
    {
        private bool released;
        public void Dispose() { if (!released) { session?.Dispose(); released = true; } mirror.Dispose(); }
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void CaptureFiles(string directory, string root, Dictionary<string, byte[]> result, HashSet<string> directories, CancellationToken cancellation)
    {
        NativeMigrationMirror.RejectLink(directory); directories.Add(Path.GetRelativePath(root, directory));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellation.ThrowIfCancellationRequested(); NativeMigrationMirror.RejectLink(entry);
            if (Directory.Exists(entry)) CaptureFiles(entry, root, result, directories, cancellation);
            else result.Add(Path.GetRelativePath(root, entry), File.ReadAllBytes(entry));
        }
    }
    private static void CaptureOriginalTree(string directory, NativeMigrationMirror mirror,
        Dictionary<string, (byte[] Bytes, OwnedFileIdentity Identity)> files,
        Dictionary<string, OwnedDirectoryIdentity> directories, CancellationToken cancellation)
    {
        NativeMigrationMirror.RejectLink(directory);
        directories.Add(Path.GetRelativePath(mirror.OriginalRoot, directory), WindowsOwnedDirectory.Capture(directory));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellation.ThrowIfCancellationRequested(); NativeMigrationMirror.RejectLink(entry);
            if (Directory.Exists(entry)) CaptureOriginalTree(entry, mirror, files, directories, cancellation);
            else
            {
                using var source = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                var identity = WindowsOwnedAssetFileWriter.CaptureIdentity(source);
                using var bytes = new MemoryStream(); source.CopyTo(bytes); var content = bytes.ToArray();
                var relative = Path.GetRelativePath(mirror.OriginalRoot, entry);
                if (mirror.Baselines.TryGetValue(relative, out var baseline) && !SHA256.HashData(content).AsSpan().SequenceEqual(baseline.Hash))
                    throw new IOException("A platform file changed while preparing: " + entry);
                files.Add(relative, (content, identity));
            }
        }
    }
    private static void RemovePrivateTree(string directory, string root)
    {
        if (!Directory.Exists(directory)) return;
        if (!NativeMigrationMirror.Inside(root, directory) || SamePath(root, directory)) throw new IOException("Invalid platform stage directory.");
        NativeMigrationMirror.RejectLink(directory); var identity = WindowsOwnedDirectory.Capture(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            NativeMigrationMirror.RejectLink(entry);
            if (Directory.Exists(entry)) RemovePrivateTree(entry, root);
            else
            {
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fileIdentity = WindowsOwnedAssetFileWriter.CaptureIdentity(stream); var hash = SHA256.HashData(stream); stream.Dispose();
                WindowsOwnedAssetFileDeletion.Delete(entry, hash, true, fileIdentity);
            }
        }
        WindowsOwnedDirectory.Remove(directory, identity);
    }
    public void Dispose() => Mirror.Dispose();
}
