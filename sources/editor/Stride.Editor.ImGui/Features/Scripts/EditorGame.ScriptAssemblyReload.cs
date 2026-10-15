using System.Reflection;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    /// <summary>Called with the actual package assembly outputs after a successful compilation.</summary>
    private void ReloadScriptAssemblies(IReadOnlyDictionary<PackageLoadedAssembly, string> outputs, ILogger log, IAssemblyArtifactLifetime? artifacts = null)
    {
        if (workspace == null || authoring == null || sessionHistory == null || projectLoading != null
            || IsUserProjectBuilding || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating)
            throw new InvalidOperationException("The authored session is not ready for assembly reload.");
        if (sessionHistory.Stack.TransactionInProgress || sessionHistory.Stack.RollInProgress || sessionHistory.IsCapturingOperations || sessionHistory.IsCompensating)
            throw new InvalidOperationException("Wait for the actual authoring transaction to complete before reloading assemblies.");
        if (outputs.Count == 0) return;
        ValidatePendingPropertyStrings();
        ClearDebugWindow();
        ClearAssetRepair();
        documents.CommitAll();
        var allOwners = workspace.Session.Packages.SelectMany(package => package.LoadedAssemblies).Distinct().ToArray();
        var shared = outputs.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in outputs)
            if (pair.Key.Assembly != null)
                foreach (var owner in allOwners.Where(owner => ReferenceEquals(owner.Assembly, pair.Key.Assembly)))
                    if (!shared.ContainsKey(owner)) shared.Add(owner, pair.Value);
        outputs = shared;
        var packages = workspace.Session.Packages.Where(package => package.LoadedAssemblies.Any(outputs.ContainsKey)).ToArray();
        if (outputs.Keys.Any(owner => !packages.Any(package => package.LoadedAssemblies.Contains(owner))))
            throw new InvalidOperationException("The compiled assembly has no actual package owner.");
        var operation = new ScriptAssemblyReloadOperation(workspace.Session.AssemblyContainer, outputs, log, artifacts);
        var assemblies = operation.OriginalAssemblies.ToHashSet();
        var leases = new List<IDisposable>();
        var projectOwners = packages.Select(package =>
        {
            var project = workspace.Projects.SingleOrDefault(project => ReferenceEquals(project.Package, package));
            return project != null
                ? (EditorAuthoringState)authoring.GetOrCreate(project.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(project, sessionHistory))
                : authoring.GetOrCreate(package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(workspace, package, sessionHistory));
        }).Distinct().ToArray();
        var retained = new List<(EditorAuthoringState State, AssetItem Item, Func<ILogger, IEditorDocument> Create)>();
        try
        {
            foreach (var item in workspace.Session.Packages.SelectMany(package => package.Assets).ToArray())
            {
                var existing = authoring.Find(item.Id);
                var asset = existing?.Document is IAssetTagDocument tagged ? tagged.AuthoredAsset : item.Asset;
                var visitor = new AssemblyReloadAssetVisitor(assemblies); visitor.Visit(asset);
                if (!visitor.Found) continue;
                if (asset is SourceCodeAsset) throw new InvalidOperationException("Source assets cannot migrate through asset YAML.");
                var source = existing?.Document ?? GetCanonicalDocument(item.FullPath.ToOSPath(), () => item.Asset switch
                {
                    SceneAsset => new SceneDocument(item.FullPath.ToOSPath(), sessionHistory, item.Package.IsReadOnly),
                    PrefabAsset => new PrefabDocument(item.FullPath.ToOSPath(), sessionHistory, item.Package.IsReadOnly),
                    ProceduralModelAsset => new ProceduralAssetDocument(item.FullPath.ToOSPath(), sessionHistory, item.Package.IsReadOnly),
                    Stride.Assets.Scripts.VisualScriptAsset => new VisualScriptDocument(item.FullPath.ToOSPath(), sessionHistory, item.Package.IsReadOnly),
                    _ => new AssetMetadataDocument(item.FullPath.ToOSPath(), sessionHistory, item.Package.IsReadOnly)
                });
                var state = authoring.Find(item.Id)!;
                leases.Add(state.AcquireView());
                switch (source)
                {
                    case EntityHierarchyDocument hierarchy:
                        var hierarchySnapshot = hierarchy.CaptureAssemblyReload();
                        retained.Add((state, item, logger => hierarchy.CreateAssemblyReloadedGeneration(hierarchySnapshot, logger))); break;
                    case ProceduralAssetDocument procedural:
                        var proceduralSnapshot = procedural.CaptureAssemblyReload(item);
                        retained.Add((state, item, logger => procedural.CreateAssemblyReloadedGeneration(proceduralSnapshot, logger))); break;
                    case AssetMetadataDocument metadata:
                        var metadataSnapshot = metadata.CaptureAssemblyReload(item);
                        retained.Add((state, item, logger => metadata.CreateAssemblyReloadedGeneration(metadataSnapshot, logger))); break;
                    default: throw new InvalidOperationException("The asset has another canonical owner without assembly migration support.");
                }
            }
            foreach (var owner in projectOwners) leases.Add(owner.AcquireView());
            using var generationBatch = BeginAuthoredGenerationBatch();
            using var capture = sessionHistory.CaptureOperationsAtomic(() =>
            {
                using var transaction = sessionHistory.CreateTransaction("Reload assemblies");
                try { operation.ApplyInitial(); }
                catch
                {
                    try { operation.CancelInitial(); }
                    catch { sessionHistory.RetainFailedInitialOperation(operation, projectOwners); throw; }
                    throw;
                }
                try { sessionHistory.PushOperation(operation, projectOwners); }
                catch (Exception failure)
                {
                    try { operation.CancelInitial(); }
                    catch (Exception recovery)
                    {
                        sessionHistory.RetainFailedInitialOperation(operation, projectOwners);
                        throw new AggregateException("Assembly preparation and restoration failed.", failure, recovery);
                    }
                    throw;
                }
                foreach (var entry in retained)
                {
                    var next = entry.Create(log);
                    EditorAuthoringState after;
                    try { after = authoring.RegisterUnpublished(next, entry.State.AssetId); }
                    catch { if (!authoring.Owns(next)) next.Dispose(); throw; }
                    leases.Add(after.AcquireView());
                    var nextItem = entry.Item.Clone(keepPackage: true, newAsset: ((IAssetTagDocument)next).AuthoredAsset);
                    void Swap(EditorAuthoringState previous, EditorAuthoringState target, AssetItem before, AssetItem afterItem)
                    {
                        var package = before.Package;
                        if (!ReferenceEquals(package.Assets.FirstOrDefault(item => item.Id == before.Id), before)) throw new InvalidOperationException("The reloaded package asset owner changed.");
                        package.Assets.Remove(before);
                        try { package.Assets.Add(afterItem); ApplyAuthoredSwap(previous, target); }
                        catch
                        {
                            package.Assets.Remove(afterItem); package.Assets.Add(before); throw;
                        }
                    }
                    Swap(entry.State, after, entry.Item, nextItem);
                    var swap = new AuthoredSwapOperation(
                        () => Swap(after, entry.State, nextItem, entry.Item),
                        () => Swap(entry.State, after, entry.Item, nextItem));
                    try { sessionHistory.PushOperation(swap, entry.State, after); }
                    catch (Exception failure)
                    {
                        try { Swap(after, entry.State, nextItem, entry.Item); }
                        catch (Exception recovery)
                        {
                            sessionHistory.RetainFailedInitialOperation(swap, entry.State, after);
                            throw new AggregateException("Hierarchy reload publication and restoration failed.", failure, recovery);
                        }
                        throw;
                    }
                }
                // All bases now resolve to the complete new generation set.
                RefreshAuthoredGenerationBatch();
                foreach (var entry in retained)
                {
                    sessionHistory.HierarchyGraphs.ReconcilePublishedBases(entry.Item.Id);
                    sessionHistory.AssetGraphs.ReconcilePublished(entry.Item.Id);
                }
            }, restore =>
            {
                using var recoveryBatch = BeginAuthoredGenerationBatch();
                restore(); RefreshAuthoredGenerationBatch();
            });
            capture.Publish(sessionHistory, "Reload assemblies", composite => new GuardedOperation(composite, ReplayAuthoredGenerationBatch));
            workspace.RefreshAssets(); assetBrowser.RefreshEntries(workspace);
            StopScriptSemantics();
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    private sealed class AssemblyReloadAssetVisitor(IReadOnlySet<Assembly> assemblies) : AssetVisitorBase
    {
        internal bool Found;
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (assemblies.Contains(obj.GetType().Assembly) || obj is Stride.Core.Yaml.IUnloadable) { Found = true; return; }
            if (!Found) base.VisitObject(obj, descriptor, visitMembers);
        }
    }
}
