using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed partial class NativeMigrationMirror
{
    private readonly Dictionary<string, string> projectContextImports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OwnedDirectoryIdentity> boundedImportDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> ancestorPolicyPresence = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> publishedProjectImports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> boundedProjectInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> projectInputCoverage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> externalProjectRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte[] Hash, OwnedFileIdentity Identity)> externalProjectFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OwnedDirectoryIdentity> externalProjectDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> externalProjectEntries = new(StringComparer.OrdinalIgnoreCase);
    private static string[] ProjectDirectoryEntries(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Where(path => !Excluded(Path.GetFileName(path))).Select(path => (Directory.Exists(path) ? "D/" : "F/") + Path.GetFileName(path))
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    internal string ProjectInputPath(string original)
    {
        var full = Path.GetFullPath(original);
        if (Inside(OriginalRoot, full)) return Path.Combine(StageRoot, Path.GetRelativePath(OriginalRoot, full));
        if (boundedProjectInputs.TryGetValue(full, out var bounded)) return bounded;
        foreach (var root in externalProjectRoots.OrderByDescending(entry => entry.Key.Length))
            if (Inside(root.Key, full) && projectInputCoverage.Any(directory => Inside(directory, full))) return Path.Combine(root.Value, Path.GetRelativePath(root.Key, full));
        throw new IOException("The native project input has not been privately staged: " + full);
    }
    internal void StageProjectRoot(string originalProject, CancellationToken token)
    {
        var project = Path.GetFullPath(originalProject);
        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new IOException("A native referenced project input is unavailable: " + project);
        if (Inside(OriginalRoot, project) || projectInputCoverage.Any(root => Inside(root, project))) return;
        var sourceRoot = Path.GetDirectoryName(project)!;
        // Preserve the real nearest/ancestor repository build policy rather than
        // importing the main game's private Directory.Build files accidentally.
        for (var ancestor = sourceRoot; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            if (new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", "nuget.config" }
                .Any(name => File.Exists(Path.Combine(ancestor, name)))) sourceRoot = ancestor;
        var boundedVolume = SameProjectRoot(sourceRoot, Path.GetPathRoot(sourceRoot)!);
        var copyRoot = boundedVolume ? Path.GetDirectoryName(project)! : sourceRoot;
        for (var parent = sourceRoot; parent != null; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
        var destination = externalProjectRoots.TryGetValue(sourceRoot, out var existingRoot) ? existingRoot
            : Path.Combine(container, "project-inputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        externalProjectRoots.TryAdd(sourceRoot, destination);
        void Copy(string directory)
        {
            token.ThrowIfCancellationRequested(); RejectLink(directory);
            var identity = WindowsOwnedDirectory.Capture(directory);
            var target = Path.Combine(destination, Path.GetRelativePath(sourceRoot, directory)); Directory.CreateDirectory(target);
            var entries = ProjectDirectoryEntries(directory);
            if (externalProjectDirectories.TryGetValue(directory, out var priorDirectory)
                && (priorDirectory != identity || !externalProjectEntries[directory].SequenceEqual(entries, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("A native overlapping project input changed during preparation: " + directory);
            externalProjectDirectories.TryAdd(directory, identity);
            externalProjectEntries.TryAdd(directory, entries);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); if (Excluded(Path.GetFileName(entry))) continue; RejectLink(entry);
                if (Directory.Exists(entry)) { Copy(entry); continue; }
                using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fileIdentity = WindowsOwnedAssetFileWriter.CaptureIdentity(input);
                var fileHash = SHA256.HashData(input); input.Position = 0;
                if (externalProjectFiles.TryGetValue(entry, out var priorFile))
                {
                    if (priorFile.Identity != fileIdentity || !fileHash.AsSpan().SequenceEqual(priorFile.Hash))
                        throw new IOException("A native overlapping project input changed during preparation: " + entry);
                    continue;
                }
                using var output = new FileStream(Path.Combine(destination, Path.GetRelativePath(sourceRoot, entry)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                externalProjectFiles.Add(entry, (fileHash, fileIdentity));
            }
        }
        Copy(copyRoot); projectInputCoverage.Add(copyRoot);
        if (boundedVolume)
            for (var ancestor = copyRoot; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", "nuget.config" })
                    {
                    RejectLink(ancestor);
                    boundedImportDirectories.TryAdd(ancestor, WindowsOwnedDirectory.Capture(ancestor));
                    var policyPath = Path.Combine(ancestor, name);
                    var exists = File.Exists(policyPath);
                    if (ancestorPolicyPresence.TryGetValue(policyPath, out var previousPresence) && previousPresence != exists)
                        throw new IOException("The native ancestor build policy changed during preparation: " + policyPath);
                    ancestorPolicyPresence.TryAdd(policyPath, exists);
                    if (exists) StageProjectImport(policyPath, token);
                }
    }
    internal string StageProjectImport(string original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var source = Path.GetFullPath(original); RejectLink(source);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        for (var ancestor = sourceDirectory; ancestor != null; ancestor = Path.GetDirectoryName(ancestor)) RejectLink(ancestor);
        var directoryIdentity = WindowsOwnedDirectory.Capture(sourceDirectory);
        if (boundedImportDirectories.TryGetValue(sourceDirectory, out var previousDirectory) && previousDirectory != directoryIdentity)
            throw new IOException("The native imported input directory changed identity: " + sourceDirectory);
        boundedImportDirectories.TryAdd(sourceDirectory, directoryIdentity);
        if (boundedProjectInputs.TryGetValue(source, out var retained)) return retained;
        string target;
        if (Inside(OriginalRoot, source)) target = Path.Combine(StageRoot, Path.GetRelativePath(OriginalRoot, source));
        else
        {
            var root = externalProjectRoots.OrderByDescending(entry => entry.Key.Length).FirstOrDefault(entry => Inside(entry.Key, source));
            target = root.Key != null ? Path.Combine(root.Value, Path.GetRelativePath(root.Key, source))
                : Path.Combine(container, "import-inputs", Guid.NewGuid().ToString("N"), Path.GetFileName(source));
        }
        if (!File.Exists(target))
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = WindowsOwnedAssetFileWriter.CaptureIdentity(input);
            using var bytes = new MemoryStream(); input.CopyTo(bytes);
            var body = bytes.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WindowsOwnedAssetFileWriter.Write(target, null, body);
            externalProjectFiles[source] = (SHA256.HashData(body), identity);
        }
        else if (!externalProjectFiles.ContainsKey(source))
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            externalProjectFiles[source] = (SHA256.HashData(input), WindowsOwnedAssetFileWriter.CaptureIdentity(input));
        }
        boundedProjectInputs.Add(source, target); return target;
    }
    internal string StageProjectImport(string original, string originalProject, CancellationToken token)
    {
        var source = Path.GetFullPath(original); var project = Path.GetFullPath(originalProject);
        var key = project + "\0" + source;
        if (projectContextImports.TryGetValue(key, out var retained)) return retained;
        var baseline = StageProjectImport(source, token);
        var context = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(project)));
        var input = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
        var target = Path.Combine(container, "import-contexts", context, input, Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        WindowsOwnedAssetFileWriter.Write(target, null, File.ReadAllBytes(baseline));
        projectContextImports.Add(key, target); return target;
    }
    internal void PublishProjectImport(string original, string originalProject, byte[] bytes, CancellationToken token)
    {
        var target = StageProjectImport(original, originalProject, token);
        if (!IsPrivateProjectInput(target)) throw new IOException("The native context import has no private ownership scope.");
        WindowsOwnedAssetFileWriter.Write(target, SHA256.HashData(File.ReadAllBytes(target)), bytes);
    }
    internal string StageProjectReference(string original, CancellationToken token)
    {
        var source = Path.GetFullPath(original);
        try { return ProjectInputPath(source); }
        catch (IOException)
        { StageProjectRoot(source, token); return ProjectInputPath(source); }
    }
    internal string StageProjectResource(string original, CancellationToken token)
    {
        var source = Path.GetFullPath(original);
        try
        {
            var target = ProjectInputPath(source);
            return File.Exists(source) && !File.Exists(target) ? StageProjectImport(source, token) : target;
        }
        catch (IOException) when (File.Exists(source)) { return StageProjectImport(source, token); }
    }
    internal void PublishProjectImport(string original, byte[] bytes, CancellationToken token)
    {
        var target = StageProjectImport(original, token);
        if (!IsPrivateProjectInput(target)) throw new IOException("The native import has no private ownership scope.");
        var source = Path.GetFullPath(original);
        if (publishedProjectImports.TryGetValue(source, out var prior))
        {
            if (!prior.AsSpan().SequenceEqual(bytes))
                throw new IOException("A shared native import has incompatible project-context staging plans: " + source);
            return;
        }
        WindowsOwnedAssetFileWriter.Write(target, SHA256.HashData(File.ReadAllBytes(target)), bytes);
        publishedProjectImports.Add(source, bytes.ToArray());
    }
    private static bool SameProjectRoot(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    internal bool IsPrivateProjectInput(string path) => taskInputRoots.Values.Any(root => Inside(root, path)) || projectContextImports.Values.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase) || Inside(StageRoot, path) || externalProjectRoots.Values.Any(root => Inside(root, path)) || boundedProjectInputs.Values.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase);
    internal void RebaseCopiedSolutions(CancellationToken token)
    {
        foreach (var file in Baselines.Values.Where(file => Path.GetExtension(file.RelativePath).ToLowerInvariant() is ".sln" or ".slnx"))
        {
            token.ThrowIfCancellationRequested();
            var staged = Path.Combine(StageRoot, file.RelativePath);
            var original = Path.Combine(OriginalRoot, file.RelativePath);
            using var source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            var solution = Stride.Core.Solutions.Solution.FromStream(original, source);
            source.Dispose();
            foreach (var project in solution.Projects.Where(project => !project.IsSolutionFolder))
                project.FullPath = ProjectInputPath(Path.GetFullPath(project.FullPath, Path.GetDirectoryName(original)!));
            solution.SaveAs(staged); // Actual native serialization of the private file only.
        }
    }
    internal void ValidateExternalProjectInputs()
    {
        ValidateTaskInputs();
        foreach (var pair in boundedImportDirectories)
            if (WindowsOwnedDirectory.Capture(pair.Key) != pair.Value)
                throw new IOException("A native bounded import directory changed identity: " + pair.Key);
        foreach (var pair in ancestorPolicyPresence)
            if (File.Exists(pair.Key) != pair.Value)
                throw new IOException("A native ancestor build policy changed presence: " + pair.Key);
        foreach (var pair in externalProjectDirectories)
            if (WindowsOwnedDirectory.Capture(pair.Key) != pair.Value || !ProjectDirectoryEntries(pair.Key).SequenceEqual(externalProjectEntries[pair.Key], StringComparer.OrdinalIgnoreCase))
                throw new IOException("A native project input directory changed identity or membership: " + pair.Key);
        foreach (var pair in externalProjectFiles)
        {
            RejectLink(pair.Key);
            using var stream = new FileStream(pair.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (WindowsOwnedAssetFileWriter.CaptureIdentity(stream) != pair.Value.Identity || !SHA256.HashData(stream).AsSpan().SequenceEqual(pair.Value.Hash))
                throw new IOException("A native external project input changed during preparation: " + pair.Key);
        }
    }
}
