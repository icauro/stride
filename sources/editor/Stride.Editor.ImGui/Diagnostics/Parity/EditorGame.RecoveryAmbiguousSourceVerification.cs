using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record RecoveryAmbiguousSourceFixture(string MainProject, string PeerProject, string PeerPackage,
    string SourcePath, string MainProjectBeforeHash, string MainProjectAfterHash, string SourceHash, string ManifestHash);

internal sealed partial class EditorGame
{
    private RecoveryAmbiguousSourceFixture? recoveryAmbiguousFixture;
    internal RecoveryAmbiguousSourceFixture PrepareRecoveryAmbiguousSourceReceiverFixture(RecoveryWorkspaceAcceptanceReceipt receipt)
    {
        if (!args.Contains("--ui-test") || !args.Contains("--recovery-ambiguous-external-source") || workspace != null)
            throw new InvalidOperationException("Ambiguous ownership arrangement requires the explicit unloaded private receiver.");
        var packagePath = WorkspaceRecoveryFixtureGuard.Resolve(projectPathInput, args);
        var root = Path.GetDirectoryName(packagePath)!;
        using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".workspace-recovery-fixture.json")));
        var main = Path.GetFullPath(marker.RootElement.GetProperty("sourceProjectPath").GetString()!);
        var owned = Path.GetFullPath(marker.RootElement.GetProperty("ownedRoot").GetString()!);
        var generated = receipt.GeneratedReadOnlySources ?? throw new InvalidDataException("Actual protected-source producer receipt is required.");
        var source = Path.GetFullPath(generated.ExternalReadOnlyPath);
        var descriptor = generated.ExternalReadOnlyDescriptor;
        if (!SamePath(receipt.Workspace.ProjectPath, packagePath) || !SamePath(descriptor.Path, source)
            || !descriptor.ReadOnlyPresentation || descriptor.SourceProjectPath != null || descriptor.AssetId != null
            || descriptor.PackagePath != null || descriptor.GeneratedByAssetId != null || descriptor.Namespace != null
            || !source.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !main.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || RecoveryAmbiguousHash(source) != generated.ExternalReadOnlySha256 || RecoveryAmbiguousHash(receipt.ManifestPath) != receipt.ManifestSha256)
            throw new InvalidDataException("Do not alter the genuine producer descriptor, source bytes or immutable checkpoint.");
        var mainBefore = RecoveryAmbiguousHash(main);
        var originalProject = receipt.OriginalDisks.SingleOrDefault(disk => SamePath(disk.Path, main));
        if (originalProject == null || originalProject.Sha256 != mainBefore)
            throw new InvalidDataException("Membership arrangement requires the actual producer's exact saved project baseline.");
        var mainXml = XDocument.Load(main, LoadOptions.PreserveWhitespace);
        var ns = mainXml.Root!.Name.Namespace;
        var framework = mainXml.Descendants(ns + "TargetFramework").Select(element => element.Value).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(framework)) throw new InvalidDataException("Actual single-target SDK fixture is required.");
        var peerRoot = Path.Combine(root, "RecoveryAmbiguousPeer");
        if (Directory.Exists(peerRoot)) throw new IOException("The native peer fixture must be independently new.");
        Directory.CreateDirectory(Path.Combine(peerRoot, "Assets"));
        var peer = Path.Combine(peerRoot, "RecoveryAmbiguousPeer.csproj");
        var peerPackage = Path.ChangeExtension(peer, ".sdpkg");
        var peerXml = new XDocument(new XElement(ns + "Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement(ns + "PropertyGroup", new XElement(ns + "TargetFramework", framework),
                new XElement(ns + "RootNamespace", "RecoveryAmbiguousPeer")),
            new XElement(ns + "ItemGroup", mainXml.Descendants(ns + "PackageReference").Select(reference => new XElement(reference)))));
        AddRealLink(peerXml, peer, source);
        peerXml.Save(peer);
        var nativePackage = new Package(); nativePackage.Meta.Name = "RecoveryAmbiguousPeer";
        nativePackage.FullPath = new UFile(peerPackage);
        AssetFileSerializer.Save(peerPackage, nativePackage, null);
        AddRealLink(mainXml, main, source);
        mainXml.Root.Add(new XElement(ns + "ItemGroup", new XElement(ns + "ProjectReference",
            new XAttribute("Include", Path.GetRelativePath(root, peer).Replace('\\', '/')))));
        mainXml.Save(main);
        recoveryAmbiguousFixture = new(main, peer, peerPackage, source, mainBefore, RecoveryAmbiguousHash(main),
            RecoveryAmbiguousHash(source), RecoveryAmbiguousHash(receipt.ManifestPath));
        if (recoveryAmbiguousFixture.SourceHash != generated.ExternalReadOnlySha256
            || recoveryAmbiguousFixture.ManifestHash != receipt.ManifestSha256)
            throw new InvalidDataException("Receiver membership arrangement modified the producer source or checkpoint.");
        WriteRecoveryAmbiguousReceipt("ambiguity-preload.json", recoveryAmbiguousFixture);
        return recoveryAmbiguousFixture;

        static void AddRealLink(XDocument xml, string project, string physical)
        {
            var itemNamespace = xml.Root!.Name.Namespace;
            var include = Path.GetRelativePath(Path.GetDirectoryName(project)!, physical).Replace('\\', '/');
            xml.Root.Add(new XElement(itemNamespace + "ItemGroup", new XElement(itemNamespace + "Compile",
                new XAttribute("Include", include), new XElement(itemNamespace + "Link", "Scripts/RecoveryAmbiguousExternal.cs"))));
        }
    }
    internal object VerifyRecoveryAmbiguousSourceAuthority(EditorRecoveryOpenDocument actualDescriptor)
    {
        var fixture = recoveryAmbiguousFixture ?? throw new InvalidOperationException("Actual receiver membership arrangement is absent.");
        var current = workspace ?? throw new InvalidOperationException("Actual receiver native workspace has not loaded.");
        if (current.Projects.Count != 2) throw new InvalidOperationException("The ambiguity fixture requires exactly its two actual native project owners.");
        if (!SamePath(actualDescriptor.Path, fixture.SourcePath) || !IsUnownedReadonlyRecoverySource(actualDescriptor))
            throw new InvalidDataException("Only the actual unmodified producer descriptor may be tested.");
        var receipts = new List<object>();
        foreach (var path in new[] { fixture.MainProject, fixture.PeerProject })
        {
            var project = current.Projects.SingleOrDefault(candidate => SamePath(candidate.FullPath.ToOSPath(), path));
            if (project == null || !current.Session.Projects.Contains(project) || !ReferenceEquals(project.Package.Container, project))
                throw new InvalidOperationException("Both real native SolutionProjects/packages must load before receiver refusal is certified.");
            using var properties = new AuthoredProjectProperties(File.ReadAllBytes(path), path);
            var contexts = properties.EvaluateDependencyContexts("Debug");
            var matched = new List<object>();
            foreach (var context in contexts)
            {
                if (!context.CompilePaths.Any(candidate => SamePath(candidate, fixture.SourcePath))) continue;
                var extraProperties = string.IsNullOrEmpty(context.Framework) ? null
                    : new Dictionary<string, string> { ["TargetFramework"] = context.Framework };
                var evaluated = VSProjectHelper.LoadProject(path, "Debug", extraProperties: extraProperties);
                try
                {
                    var items = evaluated.GetItems("Compile").Where(item => SamePath(Path.GetFullPath(item.EvaluatedInclude,
                        Path.GetDirectoryName(path)!), fixture.SourcePath)).ToArray();
                    if (items.Length != 1 || items[0].GetMetadataValue("Link") != "Scripts/RecoveryAmbiguousExternal.cs")
                        throw new InvalidOperationException("Exactly one real evaluated Compile/Link per project is required.");
                    matched.Add(new { context.Framework, include = items[0].EvaluatedInclude, link = items[0].GetMetadataValue("Link") });
                }
                finally { evaluated.ProjectCollection.UnloadAllProjects(); evaluated.ProjectCollection.Dispose(); }
            }
            if (matched.Count == 0) throw new InvalidOperationException("The loaded native peer does not actually compile the shared physical source.");
            receipts.Add(new { project = path, package = project.Package.FullPath.ToOSPath(), nativeSessionProject = true,
                nativePackageOwner = true, evaluatedContexts = matched });
        }
        var nativeItems = current.AssetItems.Where(item => SamePath(item.FullPath.ToOSPath(), fixture.SourcePath)).ToArray();
        var readonlySource = new ScriptSourceDocument(fixture.SourcePath, readOnly: true);
        var inferredOrigin = TryResolveRecoveryReadonlySourceProject(readonlySource);
        if (inferredOrigin != null) throw new InvalidOperationException("Two evaluated source owners must not be inferred as one unique origin.");
        string? refusal = null;
        try { ValidateUnownedReadonlyRecoverySource(actualDescriptor); }
        catch (InvalidOperationException error) { refusal = error.Message; }
        if (refusal == null) throw new InvalidOperationException("An originally unowned descriptor must not silently acquire native project authority.");
        var evidence = new { fixture, actualDescriptor, loadedProjectCount = receipts.Count, projects = receipts,
            inferredOrigin, originalDescriptorPreflightRefused = true, preflightFailure = refusal,
            nativeSourceAssets = nativeItems.Select(item => new { id = item.Id.ToString(), path = item.FullPath.ToOSPath(),
                alternativePath = item.AlternativePath?.ToOSPath(), package = item.Package.FullPath.ToOSPath() }).ToArray() };
        WriteRecoveryAmbiguousReceipt("ambiguity-native-authority.json", evidence);
        return evidence;
    }
    internal RecoveryWorkspaceActionReceipt? InspectRecoveryWorkspaceAction() => ownedRecovery?.LastWorkspaceAction;
    internal string[] RecoveryAmbiguousReceiverFiles => recoveryAmbiguousFixture == null ? [] :
        [recoveryAmbiguousFixture.MainProject, recoveryAmbiguousFixture.PeerProject, recoveryAmbiguousFixture.PeerPackage, recoveryAmbiguousFixture.SourcePath];
    private static string RecoveryAmbiguousHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private void WriteRecoveryAmbiguousReceipt(string name, object value)
    {
        var artifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
            ?? throw new InvalidOperationException("Actual receiver private artifacts are required.");
        File.WriteAllText(Path.Combine(artifacts, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
