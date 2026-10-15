using System.Security.Cryptography;
using NuGet.ProjectModel;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Actual MSBuild/NuGet restore files, replayed with their resolved dependency operation.</summary>
internal sealed class ProjectDependencyRestoreArtifacts
{
    private sealed record FileState(byte[] Bytes, byte[] Hash);
    private readonly string output, projectName, assets, cache;
    private readonly string? packageLock;
    private readonly Dictionary<string, FileState> before;
    private Dictionary<string, FileState>? after;
    private ProjectDependencyRestoreArtifacts(string output, string projectName, string assets, string cache, string? packageLock)
    {
        this.output = output; this.projectName = projectName; this.assets = assets; this.cache = cache; this.packageLock = packageLock;
        before = Capture();
    }
    internal string AssetsPath => assets;
    internal static ProjectDependencyRestoreArtifacts Prepare(string projectPath)
    {
        var project = VSProjectHelper.LoadProject(projectPath);
        try
        {
            var directory = Path.GetDirectoryName(projectPath)!;
            var assets = project.GetPropertyValue("ProjectAssetsFile");
            if (string.IsNullOrWhiteSpace(assets)) throw new InvalidOperationException("The evaluated dependency assets path is unavailable.");
            assets = Path.GetFullPath(assets, directory);
            var nativeOutput = project.GetPropertyValue("RestoreOutputPath");
            if (string.IsNullOrWhiteSpace(nativeOutput)) nativeOutput = project.GetPropertyValue("MSBuildProjectExtensionsPath");
            var output = string.IsNullOrWhiteSpace(nativeOutput) ? Path.GetDirectoryName(assets)! : Path.GetFullPath(nativeOutput, directory);
            var cache = Path.Combine(output, "project.nuget.cache");
            if (File.Exists(assets))
            {
                var previous = new LockFileFormat().Read(assets);
                if (!string.IsNullOrWhiteSpace(previous.PackageSpec?.RestoreMetadata?.CacheFilePath))
                    cache = Path.GetFullPath(previous.PackageSpec.RestoreMetadata.CacheFilePath, directory);
            }
            var lockPath = project.GetPropertyValue("NuGetLockFilePath");
            var packagesLock = Path.GetFullPath(string.IsNullOrWhiteSpace(lockPath) ? PackagesLockFileFormat.LockFileName : lockPath, directory);
            var ownsLock = File.Exists(packagesLock) || bool.TryParse(project.GetPropertyValue("RestorePackagesWithLockFile"), out var enabled) && enabled;
            return new(output, Path.GetFileName(projectPath), assets, cache, ownsLock ? packagesLock : null);
        }
        finally { project.ProjectCollection.UnloadAllProjects(); project.ProjectCollection.Dispose(); }
    }
    internal void Complete() => after = Capture();
    internal Operation CreateOperation() => new RestoreOperation(this);
    private sealed class RestoreOperation(ProjectDependencyRestoreArtifacts artifacts) : Operation
    {
        protected override void Rollback() => artifacts.Replay(false);
        protected override void Rollforward() => artifacts.Replay(true);
    }
    private Dictionary<string, FileState> Capture()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { assets, cache };
        if (packageLock != null) paths.Add(packageLock);
        ValidatePath(output);
        if (Directory.Exists(output))
            foreach (var path in Directory.EnumerateFiles(output, projectName + ".nuget.*", SearchOption.TopDirectoryOnly)) paths.Add(path);
        var snapshot = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ValidatePath(path);
            if (!File.Exists(path)) continue;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var bytes = new MemoryStream(); input.CopyTo(bytes); var contents = bytes.ToArray();
            snapshot.Add(path, new(contents, SHA256.HashData(contents)));
        }
        return snapshot;
    }
    internal void Replay(bool forward)
    {
        if (after == null) throw new InvalidOperationException("Dependency restore outputs have not been captured.");
        var expected = forward ? before : after; var target = forward ? after : before;
        var changes = expected.Keys.Union(target.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !Equal(expected.GetValueOrDefault(path), target.GetValueOrDefault(path))).ToArray();
        foreach (var path in changes) Verify(path, expected.GetValueOrDefault(path));
        var applied = new List<string>();
        try
        {
            foreach (var path in changes) { Write(path, expected.GetValueOrDefault(path), target.GetValueOrDefault(path)); applied.Add(path); }
        }
        catch (Exception failure)
        {
            var failures = new List<Exception>();
            foreach (var path in applied.AsEnumerable().Reverse())
                try { Write(path, target.GetValueOrDefault(path), expected.GetValueOrDefault(path)); }
                catch (Exception recovery) { failures.Add(recovery); }
            if (failures.Count > 0) throw new AggregateException("Dependency output replay and restoration failed.", new[] { failure }.Concat(failures));
            throw;
        }
    }
    private static bool Equal(FileState? left, FileState? right) => left == null ? right == null
        : right != null && left.Hash.AsSpan().SequenceEqual(right.Hash);
    private static void Verify(string path, FileState? expected)
    {
        ValidatePath(path);
        if (expected == null)
        { if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A dependency output appeared outside the editor: " + path); return; }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!SHA256.HashData(input).AsSpan().SequenceEqual(expected.Hash)) throw new IOException("A dependency output changed outside the editor: " + path);
    }
    private static void Write(string path, FileState? expected, FileState? value)
    {
        Verify(path, expected);
        if (value != null) WindowsOwnedAssetFileWriter.Write(path, expected?.Hash, value.Bytes);
        else WindowsOwnedAssetFileDeletion.Delete(path, expected!.Hash);
    }
    private static void ValidatePath(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Dependency restore history cannot follow a linked path: " + current);
    }
}
