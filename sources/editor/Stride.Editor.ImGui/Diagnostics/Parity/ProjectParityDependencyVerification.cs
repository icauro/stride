using System.Xml.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Packages;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>One private project, native dependency edits and exact saved-boundary history replay.</summary>
internal static class ProjectParityDependencyVerification
{
    internal static object Verify(string ownedDirectory)
    {
        Directory.CreateDirectory(ownedDirectory);
        var path = Path.Combine(ownedDirectory, "DependencyOwner.csproj");
        if (File.Exists(path)) throw new IOException("The project parity fixture already exists.");
        File.WriteAllText(path, $"<Project><PropertyGroup><{SolutionProject.ContainsAssetTypesProperty}>false</{SolutionProject.ContainsAssetTypesProperty}></PropertyGroup></Project>");
        var original = File.ReadAllBytes(path);
        using var session = new PackageSession();
        var package = new Package { FullPath = Path.Combine(ownedDirectory, "DependencyOwner.sdpkg") };
        var project = new SolutionProject(package, Guid.NewGuid(), path);
        session.Projects.Add(project);
        var registry = new EditorAuthoringRegistry();
        var history = new EditorSessionHistory(registry);
        var owner = registry.GetOrCreate(path, null, () => new ProjectBuildSettingsDocument(project, history));
        var view = owner.AcquireView();
        Exception? primary = null;
        try
        {
        var document = owner.Document;
        document.SetContains(true);
        var priorRevision = document.AuthoringRevision;
        Require(document.IsDirty && project.ContainsAssetTypes == true, "Pending unrelated project edit must be dirty.");
        var dependency = new DependencyRange("ParityDependency", new PackageVersionRange(new PackageVersion("1.0.0"), true), DependencyType.Package);
        ChangeAndPersist(document, history, null, dependency);
        var savedAdded = File.ReadAllBytes(path);
        Require(!document.IsDirty && project.DirectDependencies.Single().Name == dependency.Name,
            "The grouped add must save actual native dependencies and prior project edits.");
        Require(ReadVersion(path) == dependency.VersionRange.ToString(), "Independent XML reload must contain added package version.");
        history.Undo();
        Require(project.DirectDependencies.Count == 0 && document.AuthoringRevision == priorRevision && document.IsDirty
            && project.ContainsAssetTypes == true && File.ReadAllBytes(path).SequenceEqual(original),
            "Undo must restore exact disk bytes while retaining the earlier unrelated edit as dirty.");
        history.Redo();
        Require(!document.IsDirty && File.ReadAllBytes(path).SequenceEqual(savedAdded), "Redo must restore exact added-dependency bytes and clean boundary.");
        var current = project.DirectDependencies.Single();
        var replacement = new DependencyRange(current.Name, new PackageVersionRange(new PackageVersion("2.0.0"), true), current.Type);
        ChangeAndPersist(document, history, current, replacement);
        var savedUpdated = File.ReadAllBytes(path);
        Require(ReadVersion(path) == replacement.VersionRange.ToString(), "Updated native dependency version must persist.");
        history.Undo(); Require(File.ReadAllBytes(path).SequenceEqual(savedAdded), "Update undo must restore original saved version.");
        history.Redo(); Require(File.ReadAllBytes(path).SequenceEqual(savedUpdated), "Update redo must restore exact updated bytes.");
        ChangeAndPersist(document, history, project.DirectDependencies.Single(), null);
        var savedRemoved = File.ReadAllBytes(path);
        Require(project.DirectDependencies.Count == 0 && ReadVersion(path) == null, "Removal must change native collection and persisted PackageReference.");
        history.Undo(); Require(project.DirectDependencies.Count == 1 && File.ReadAllBytes(path).SequenceEqual(savedUpdated), "Removal undo must restore dependency and exact bytes.");
        history.Redo(); Require(project.DirectDependencies.Count == 0 && File.ReadAllBytes(path).SequenceEqual(savedRemoved), "Removal redo must restore absence and exact bytes.");
        Require(session.Projects.Count == 1 && ReferenceEquals(session.Projects.Single(), project)
            && ReferenceEquals(package.Session, session), "Dependency editing must preserve actual session project membership.");
        return new { passed = true, addUpdateRemoveRoundtrip = true, mixedDirtySaveBoundary = true,
            nativeMembershipRetained = true, scope = "Native authored dependency model/history/XML; no network restore or solution-root removal claim" };
    }

        catch (Exception error) { primary = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            foreach (var release in new Action[] { view.Dispose, history.Dispose, registry.Dispose })
                try { release(); } catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
                throw new AggregateException("Project parity verification cleanup failed.", primary == null ? cleanup : new[] { primary }.Concat(cleanup));
        }
    }

    private static void ChangeAndPersist(ProjectBuildSettingsDocument document, EditorSessionHistory history, DependencyRange? before, DependencyRange? after)
    {
        using var persistence = document.BeginDependencyRestorePersistence();
        using var transaction = history.CreateTransaction("Verify grouped dependency persistence");
        using var capture = history.CaptureOperationsAtomic(() =>
        {
            document.ChangeDependency(before, after, _ => { });
            persistence.Save();
        });
        history.PushOperation(new CompositeOperation(capture.Entries.Select(entry => entry.Operation)), capture.Participants);
    }
    private static string? ReadVersion(string path) => XDocument.Load(path).Descendants()
        .SingleOrDefault(element => element.Name.LocalName == "PackageReference" && (string?)element.Attribute("Include") == "ParityDependency")?.Attribute("Version")?.Value;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
