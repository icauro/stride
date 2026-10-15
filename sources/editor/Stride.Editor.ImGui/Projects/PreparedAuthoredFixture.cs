using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal enum ProjectWorkspaceLoadMode { FullCompilation, PreparedAuthoredFixture }

/// <summary>Immutable test preparation receipt; every consumer still owns a fresh native session and authored graphs.</summary>
internal static class PreparedAuthoredFixture
{
    internal const string ManifestName = "prepared-authored-fixture.json";
    private sealed record Input(string Path, string Hash);
    private sealed record Receipt(int Schema, string PackageName, string HostIdentity, Input[] Inputs, Input[] Assemblies, Guid ScriptMvid);
    private static string HostIdentity => string.Join("/", new[]
    {
        typeof(PreparedAuthoredFixture).Assembly.ManifestModule.ModuleVersionId,
        typeof(Stride.Engine.Entity).Assembly.ManifestModule.ModuleVersionId,
        typeof(Stride.Assets.Entities.SceneAsset).Assembly.ManifestModule.ModuleVersionId,
        typeof(PackageSession).Assembly.ManifestModule.ModuleVersionId,
    });

    internal static int PrepareCopy(string sourcePackage, string receiptPath)
    {
        string? packagePath = null;
        var passed = false;
        string? errorText = null;
        try
        {
            receiptPath = Path.GetFullPath(receiptPath);
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new { schema = 1, passed = false, phase = "preparing" }));
            packagePath = UiWorkflowTest.CopyFixture(sourcePackage);
            if (Prepare(packagePath) != 0) throw new InvalidOperationException("Native authored fixture preparation failed; see process stderr and native build logs.");
            ValidateBeforeLoad(packagePath);
            passed = true;
        }
        catch (Exception error) { errorText = error.ToString(); Console.Error.WriteLine(error); }
        finally
        {
            try
            {
                File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
                {
                    schema = 1,
                    passed,
                    packagePath,
                    manifestPath = packagePath == null ? null : Path.Combine(Path.GetDirectoryName(packagePath)!, ManifestName),
                    error = errorText,
                    ownership = "Disposable prepared files only; cases create fresh native sessions and authoring graphs",
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception reportingError) { passed = false; Console.Error.WriteLine(reportingError); }
        }
        return passed ? 0 : 1;
    }

    internal static int Prepare(string packagePath)
    {
        try
        {
            packagePath = Path.GetFullPath(packagePath);
            var root = Path.GetDirectoryName(packagePath)!;
            var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
            // Runner preparation must itself use a disposable copy, not the user's project.
            if (!packagePath.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prepare authored fixtures inside a disposable Stride-ImGui-UI project copy.");
            var workspace = new ProjectWorkspace(packagePath);
            try
            {
                if (workspace.CompilationFailure != null) throw new InvalidOperationException("Fixture preparation failed.", workspace.CompilationFailure);
                var projectName = Path.GetFileNameWithoutExtension(workspace.BuildProjectPath);
                var scriptPath = Directory.EnumerateFiles(Path.Combine(root, "bin"), projectName + ".dll", SearchOption.AllDirectories)
                    .Where(path => path.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    ?? throw new FileNotFoundException("Actual fixture script binary missing after native preparation.");
                var scriptMvid = ReadMvid(scriptPath);
                if (!workspace.Session.LocalPackages.SelectMany(package => package.LoadedAssemblies)
                    .Any(assembly => assembly.Assembly?.ManifestModule.ModuleVersionId == scriptMvid))
                    throw new InvalidDataException("The prepared binary is not the script assembly loaded by the actual native session.");
                var inputs = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                    .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets" or ".sdpkg")
                    .Select(path => Capture(root, path)).OrderBy(input => input.Path, StringComparer.Ordinal).ToArray();
                var receipt = new Receipt(1, Path.GetFileName(packagePath), HostIdentity, inputs,
                    [Capture(root, scriptPath)], scriptMvid);
                File.WriteAllText(Path.Combine(root, ManifestName), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { workspace.ReleaseUnadopted(); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    internal static ProjectWorkspace Open(string packagePath)
        => new(packagePath, loadMode: ProjectWorkspaceLoadMode.PreparedAuthoredFixture);

    internal static void ValidateBeforeLoad(string packagePath)
    {
        var receipt = Read(packagePath);
        if (receipt.Schema != 1 || receipt.PackageName != Path.GetFileName(packagePath) || receipt.HostIdentity != HostIdentity)
            throw new InvalidDataException("Prepared authored fixture does not match the current host/package identity.");
        var root = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        foreach (var input in receipt.Inputs.Concat(receipt.Assemblies))
        {
            var path = Path.GetFullPath(Path.Combine(root, input.Path));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || Hash(path) != input.Hash)
                throw new InvalidDataException("Prepared authored fixture input changed or is missing: " + input.Path);
        }
    }

    internal static void ValidateLoadedSession(string packagePath, PackageSession session)
    {
        var receipt = Read(packagePath);
        if (!session.LocalPackages.SelectMany(package => package.LoadedAssemblies)
            .Any(assembly => assembly.Assembly?.ManifestModule.ModuleVersionId == receipt.ScriptMvid))
            throw new InvalidDataException("Native authored fixture did not load its actual prepared script assembly.");
        foreach (var asset in session.LocalPackages.SelectMany(package => package.Assets))
        {
            var detector = new UnresolvedFixtureDetector(); detector.Visit(asset.Asset);
            if (detector.Found) throw new InvalidDataException("Prepared authored fixture contains an unresolved native asset/script: " + asset.Location);
        }
    }
    private sealed class UnresolvedFixtureDetector : Stride.Core.Assets.Visitors.AssetVisitorBase
    {
        internal bool Found;
        public override void VisitObject(object obj, Stride.Core.Reflection.ObjectDescriptor descriptor, bool visitMembers)
        {
            if (obj is Stride.Core.Yaml.IUnloadable) Found = true;
            base.VisitObject(obj, descriptor, visitMembers);
        }
    }
    private static Receipt Read(string packagePath) => JsonSerializer.Deserialize<Receipt>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packagePath))!, ManifestName)))
        ?? throw new InvalidDataException("Prepared authored fixture receipt missing or invalid.");
    private static Input Capture(string root, string path) => new(Path.GetRelativePath(root, path), Hash(path));
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static Guid ReadMvid(string path)
    {
        using var file = File.OpenRead(path); using var pe = new PEReader(file);
        var reader = pe.GetMetadataReader(); return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }
}
