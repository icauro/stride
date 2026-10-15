using System.Security.Cryptography;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object? AssetBrowserFilterParityEvidence { get; private set; }

    // Real browser control-model and canonical authoring services. No UI gesture
    // acceptance is inferred from these production-model calls.
    internal void VerifyAssetBrowserFilterParity()
    {
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Controls.Enabled || workspace == null || authoring == null || sessionHistory == null
            || !Path.GetFullPath(workspace.PackagePath).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase)
            || IsPackageMetadataSaving || AssetTemplatePublicationPending)
            throw new InvalidOperationException("Browser parity requires an idle loaded disposable project.");
        var nativeWorkspace = workspace; var history = sessionHistory;
        var fixture = new[]
        {
            nativeWorkspace.Assets.Single(entry => entry.Name == "Ground" && entry.AssetType == typeof(ProceduralModelAsset)
                && ReferenceEquals(nativeWorkspace.FindAsset(entry.Id).Package, nativeWorkspace.Package)),
            nativeWorkspace.Assets.Single(entry => entry.Name == "Sphere" && entry.AssetType == typeof(ProceduralModelAsset)
                && ReferenceEquals(nativeWorkspace.FindAsset(entry.Id).Package, nativeWorkspace.Package)),
            nativeWorkspace.Assets.Single(entry => entry.Name == "MainScene" && entry.AssetType == typeof(SceneAsset)
                && ReferenceEquals(nativeWorkspace.FindAsset(entry.Id).Package, nativeWorkspace.Package))
        };
        var liveSelection = assetBrowser.CaptureSelection(); var livePrimary = assetBrowser.Primary;
        var liveFilters = assetBrowser.CaptureFilters(); var liveScope = assetBrowser.CaptureNavigationScope();
        var liveSort = assetBrowser.Sort; var activeEntry = documents.Active;
        var owners = fixture.Select(entry => GetAssetTagDocument(entry, activate: false, openView: false)).ToArray();
        var states = fixture.Select(entry => authoring.Find(entry.Id)
            ?? throw new InvalidOperationException("A browser fixture asset has no canonical owner.")).ToArray();
        if (owners.Where((owner, index) => !ReferenceEquals(owner, states[index].Document)).Any()
            || owners.Any(owner => !owner.CanSave || owner.IsDirty))
            throw new InvalidOperationException("Browser tag fixtures must retain clean editable canonical owners.");
        var leases = states.Select(state => state.AcquireView()).ToArray();
        var originals = owners.Select(owner => owner.AuthoredAsset.Tags.ToArray()).ToArray();
        var sourceBytes = fixture.Select(entry => File.ReadAllBytes(entry.Path)).ToArray();
        var originalBytes = sourceBytes;
        var baseline = history.History.Position;
        var prefix = history.History.Entries.Take(baseline).Select(entry => entry.Id).ToArray();
        var bucket = "BrowserBucket-" + Guid.NewGuid().ToString("N");
        var subset = "BrowserSubset-" + Guid.NewGuid().ToString("N");
        var model = new AssetBrowserState();
        Guid? transaction = null;
        Exception? failure = null;
        var checks = new List<object>();
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        void Canonical()
        {
            Require(ReferenceEquals(workspace, nativeWorkspace) && ReferenceEquals(sessionHistory, history), "Browser verification lost its native workspace/history.");
            for (var index = 0; index < states.Length; index++)
                Require(ReferenceEquals(authoring.Find(fixture[index].Id), states[index]) && states[index].IsPublished
                    && ReferenceEquals(states[index].Document, owners[index]), "Browser verification replaced a canonical tag owner.");
        }
        void SyncTags()
        {
            Canonical();
            for (var index = 0; index < fixture.Length; index++) model.UpdateAuthoredTags(fixture[index].Id, owners[index].AuthoredAsset.Tags);
        }
        void Visible(string trigger, params int[] expected)
        {
            SyncTags();
            var actual = model.Visible.Select(entry => entry.Id).ToArray();
            var ids = expected.Select(index => fixture[index].Id).ToArray();
            Require(actual.SequenceEqual(ids), trigger + " returned different actual native asset IDs/order.");
            checks.Add(new { trigger, ids = actual.Select(id => id.ToString()).ToArray(), selected = model.CaptureSelection().Select(id => id.ToString()).ToArray(),
                primary = model.Primary?.ToString(), filters = model.CaptureFilters(), sort = model.Sort.ToString() });
        }
        string[] SavedTags(int index)
        {
            var item = nativeWorkspace.FindAsset(fixture[index].Id);
            var loaded = AssetFileSerializer.Load<Asset>(fixture[index].Path,
                assetNamespace: item.Package.Container?.AssetNamespace ?? item.Package.AssetNamespace).Asset;
            Require(loaded.Id == item.Id && loaded.GetType() == owners[index].AuthoredAsset.GetType(), "Saved browser tag asset lost its native ID/type.");
            return loaded.Tags.ToArray();
        }
        try
        {
            Canonical();
            var nativeAssets = owners.Select(owner => owner.AuthoredAsset).ToArray();
            object? Graph(IAssetTagDocument owner) => owner is SceneDocument scene ? scene.HierarchyGraph
                : owner is IEditorAssetGraphDocument graph ? graph.AuthoringGraph : null;
            var nativeGraphs = owners.Select(Graph).ToArray(); var revisions = owners.Select(owner => owner.EditRevision).ToArray();
            var nativeTransactions = history.Stack.RetrieveAllTransactions().ToArray(); var nativeUndo = history.Stack.NextUndoTransaction;
            // Real clean native Save establishes serializer casing/default metadata.
            // Keep exact byte assertions after this certified fixture preparation.
            foreach (var owner in owners) owner.Save();
            Canonical();
            Require(history.History.Position == baseline && nativeTransactions.SequenceEqual(history.Stack.RetrieveAllTransactions())
                && ReferenceEquals(nativeUndo, history.Stack.NextUndoTransaction), "Clean native Save changed actual history transactions.");
            for (var index = 0; index < owners.Length; index++)
                Require(ReferenceEquals(nativeAssets[index], owners[index].AuthoredAsset) && ReferenceEquals(nativeGraphs[index], Graph(owners[index]))
                    && owners[index].EditRevision == revisions[index] && !owners[index].IsDirty
                    && owners[index].AuthoredAsset.Tags.SequenceEqual(originals[index]), "Native serializer preparation changed canonical graph/authored state.");
            originalBytes = fixture.Select(entry => File.ReadAllBytes(entry.Path)).ToArray();
            checks.Add(new { trigger = "Clean native serializer baseline", sourceSha = sourceBytes.Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))).ToArray(),
                canonicalSha = originalBytes.Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))).ToArray(), historyUnchanged = true,
                canonicalAssetsAndGraphsRetained = true });
            model.Bind(nativeWorkspace);
            var root = model.Packages.Single(folder => ReferenceEquals(folder.Package, nativeWorkspace.Package));
            model.SelectFolder(root.Children.Single(folder => !folder.IsCode && folder.RelativePath.Length == 0));
            model.DisplayMode = AssetBrowserDisplayMode.RecursiveAssets;
            using (var capture = history.CaptureOperationsAtomic(() =>
            {
                for (var index = 0; index < owners.Length; index++)
                    owners[index].SetTags(index == 1 ? [.. originals[index], bucket] : [.. originals[index], bucket, subset]);
            })) capture.Publish(history, "Browser compound filter tags");
            Require(history.History.Position == baseline + 1, "Grouped native tags must publish exactly one transaction.");
            transaction = history.History.Entries[baseline].Id;
            Require(history.GetParticipants(history.Stack.RetrieveAllTransactions().ElementAt(baseline)).Length == owners.Length,
                "Grouped browser tag journal must retain all three canonical participants.");
            SyncTags();
            model.CommitFilter(new(AssetFilterCategory.Tag, bucket), false);
            model.Sort = AssetBrowserSort.Name; Visible("Name order", 0, 2, 1);
            model.Sort = AssetBrowserSort.TypeThenName; Visible("Type then name order", 2, 0, 1);
            model.CommitFilter(new(AssetFilterCategory.Tag, subset), false); Visible("Tag intersection", 2, 0);
            model.CommitFilter(new(AssetFilterCategory.Type, "ProceduralModelAsset", typeof(ProceduralModelAsset)), false);
            Visible("Type and tags intersection", 0);
            model.ToggleTypeEnabled(typeof(ProceduralModelAsset)); Visible("Disabled type retains tag intersection", 2, 0);
            model.ToggleTypeEnabled(typeof(ProceduralModelAsset));
            model.ClearFilters(); model.CommitFilter(new(AssetFilterCategory.Tag, bucket), false);
            model.Select(fixture[0], toggle: false, range: false); model.Select(fixture[1], toggle: true, range: false);
            var retainedSelection = model.CaptureSelection().ToHashSet();
            Require(retainedSelection.SetEquals([fixture[0].Id, fixture[1].Id]) && model.Primary == fixture[1].Id,
                "Native multi-select did not retain two distinct IDs and the last-selected primary.");
            model.CommitFilter(new(AssetFilterCategory.Tag, subset), false);
            model.CommitFilter(new(AssetFilterCategory.Type, "SceneAsset", typeof(SceneAsset)), false);
            // The production/WPF policy pins selected assets despite an excluding filter.
            Visible("Selected identities remain visible with filtered scene", 2, 0, 1);
            Require(model.CaptureSelection().ToHashSet().SetEquals(retainedSelection) && model.Primary == fixture[1].Id,
                "Filter changes redirected selected native IDs or primary.");
            model.RestoreSelection([], null); Visible("Selection removal reveals exact filtered result", 2);
            model.ClearFilters();
            Require(model.CaptureFilters().Length == 0 && model.Visible.Any(entry => entry.Id == fixture[0].Id)
                && model.Visible.Any(entry => entry.Id == fixture[1].Id), "Clear failed to restore real unfiltered assets.");
            foreach (var owner in owners) owner.Save();
            for (var index = 0; index < owners.Length; index++)
                Require(SavedTags(index).SequenceEqual(owners[index].AuthoredAsset.Tags), "Independent native YAML did not retain edited browser tags.");
            var editedBytes = fixture.Select(entry => File.ReadAllBytes(entry.Path)).ToArray();
            history.Undo();
            Require(history.History.Position == baseline && history.History.Entries[baseline].Id == transaction
                && !history.History.Entries[baseline].IsDone, "Undo changed a different grouped tag transaction.");
            for (var index = 0; index < owners.Length; index++) Require(owners[index].AuthoredAsset.Tags.SequenceEqual(originals[index]), "Undo did not restore original native tags.");
            history.Redo();
            Require(history.History.Position == baseline + 1 && history.History.Entries[baseline].Id == transaction,
                "Redo changed the grouped native tag transaction identity.");
            foreach (var owner in owners) owner.Save();
            for (var index = 0; index < owners.Length; index++)
                Require(File.ReadAllBytes(fixture[index].Path).SequenceEqual(editedBytes[index])
                    && SavedTags(index).SequenceEqual(owners[index].AuthoredAsset.Tags), "Redo changed serialized native tags/bytes.");
            checks.Add(new { trigger = "Native tags Save/Undo/Redo", transaction, canonicalGenerations = states.Select(state => state.Generation).ToArray(),
                serializedSha = editedBytes.Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))).ToArray() });
        }
        catch (Exception error) { failure = error; }
        finally
        {
            var cleanup = new List<Exception>();
            void Retire(Action action) { try { action(); } catch (Exception error) { cleanup.Add(error); } }
            Retire(() =>
            {
                Canonical();
                if (transaction != null && history.History.Position == baseline + 1 && history.History.Entries[baseline].Id == transaction) history.Undo();
                Require(history.History.Position == baseline && history.History.Entries.Take(baseline).Select(entry => entry.Id).SequenceEqual(prefix),
                    "Browser cleanup cannot redirect another native history operation.");
            });
            for (var index = 0; index < owners.Length; index++)
            {
                var ownerIndex = index;
                Retire(() =>
                {
                    Canonical();
                    Require(owners[ownerIndex].AuthoredAsset.Tags.SequenceEqual(originals[ownerIndex]), "Browser cleanup retained edited native tags.");
                    if (owners[ownerIndex].IsDirty) owners[ownerIndex].Save();
                    Require(File.ReadAllBytes(fixture[ownerIndex].Path).SequenceEqual(originalBytes[ownerIndex]), "Browser cleanup changed canonical baseline asset bytes.");
                });
            }
            Retire(() =>
            {
                Require(assetBrowser.CaptureSelection().SequenceEqual(liveSelection) && assetBrowser.Primary == livePrimary
                    && assetBrowser.CaptureFilters().SequenceEqual(liveFilters) && assetBrowser.Sort == liveSort
                    && assetBrowser.CaptureNavigationScope().SameScope(liveScope) && ReferenceEquals(documents.Active, activeEntry),
                    "Isolated browser-model verification changed the owning editor's navigation/selection/document.");
            });
            Retire(() => model.Bind(null));
            foreach (var lease in leases) Retire(lease.Dispose);
            if (cleanup.Count != 0)
            { if (failure != null) cleanup.Insert(0, failure); failure = new AggregateException("Browser verification or native cleanup failed.", cleanup); }
        }
        AssetBrowserFilterParityEvidence = new { caseId = "asset-templates.browser-compound-filters", checks, transaction,
            baselineHistory = baseline, baselineAssetIds = fixture.Select(entry => entry.Id.ToString()).ToArray(),
            sourceScope = "Actual production AssetBrowserState bound to the loaded private native workspace; isolated control model plus canonical tag/history/Save services, not synthetic UI gestures.",
            owningViewUnchanged = failure == null, cleanupCompleted = failure == null, error = failure?.ToString() };
        if (failure != null) throw failure;
    }
}
