using System.Security.Cryptography;
using Stride.Assets.Models;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object? AssetInheritanceParityEvidence { get; private set; }
    private sealed class AssetInheritanceParitySave(Action verify, IDisposable retention)
    {
        internal Action Verify { get; } = verify;
        internal IDisposable Retention { get; } = retention;
        internal bool Requested, Settled, Released;
        internal Exception? Error;
    }
    private AssetInheritanceParitySave? assetInheritanceParitySave;

    internal bool StepAssetInheritanceParityFinalSave()
    {
        var run = assetInheritanceParitySave ?? throw new InvalidOperationException("No native inheritance save is retained.");
        if (!run.Requested)
        {
            run.Requested = true;
            try { SaveAllDocuments(() => run.Settled = true, error => { run.Error = error; run.Settled = true; }); }
            catch (Exception error) { run.Error = error; run.Settled = true; }
        }
        // Never release the source owner before the actual noncancellable save terminal.
        if (!run.Settled || IsPackageMetadataSaving) return false;
        try
        {
            if (run.Error != null) throw run.Error;
            run.Verify();
            return true;
        }
        finally { if (!run.Released) { run.Retention.Dispose(); run.Released = true; } }
    }

    internal bool TryRetireAssetInheritanceParityVerification()
    {
        var run = assetInheritanceParitySave;
        if (run == null) return true;
        if (IsPackageMetadataSaving || run.Requested && !run.Settled) return false;
        if (!run.Released) { run.Retention.Dispose(); run.Released = true; }
        assetInheritanceParitySave = null;
        return true;
    }

    // Actual native creation/link commands and shared journal; no UI, importer or preview claim.
    internal void VerifyAssetInheritanceParity()
    {
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Controls.Enabled || workspace == null || authoring == null || sessionHistory == null
            || !Path.GetFullPath(workspace.PackagePath).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase)
            || !AssetTemplateParityReady || IsPackageMetadataSaving || pickerCreationScope != null || assetInheritanceParitySave != null)
            throw new InvalidOperationException("Asset inheritance verification requires an idle disposable native project.");
        var artifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
            ?? throw new InvalidOperationException("Native inheritance verification requires its retained artifact directory.");
        var yamlArtifacts = Path.Combine(Path.GetFullPath(artifacts), "asset-inheritance", "native-yaml");
        var currentWorkspace = workspace; var history = sessionHistory;
        var source = currentWorkspace.Assets.Single(entry => entry.Name == "Ground" && entry.AssetType == typeof(ProceduralModelAsset)
            && ReferenceEquals(currentWorkspace.FindAsset(entry.Id).Package, currentWorkspace.Package));
        var baseOwner = GetAssetTagDocument(source, activate: false, openView: false);
        var baseState = authoring.Find(source.Id) ?? throw new InvalidOperationException("Inheritance fixture has no canonical base owner.");
        using var baseLease = baseState.AcquireView();
        if (baseOwner.IsDirty || !baseOwner.CanSave || !CanCreateDerivedAsset(source) || !CanCreateReplacingAsset(source))
            throw new InvalidOperationException("Inheritance fixture must be clean and support both stock native creation routes.");
        var selection = CaptureCreationSelection(assetBrowser); var originalViews = documents.Entries.ToArray();
        var originalActive = documents.Active;
        var baseline = history.History.Position; var prefix = history.History.Entries.Take(baseline).Select(entry => entry.Id).ToArray();
        var baselineIds = AssetTemplateNativeIds();
        var sourceBytes = File.ReadAllBytes(source.Path); var baseRevision = baseOwner.EditRevision;
        var checks = new List<object>(); var ownedTransactions = new HashSet<Guid>();
        var children = new List<(AssetEntry Entry, IAssetTagDocument Owner, EditorAuthoringState State)>();
        byte[]? canonicalBaseBytes = null;
        Exception? failure = null;
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        void Bytes(AssetEntry entry, byte[] expected, string phase)
        {
            var actual = File.ReadAllBytes(entry.Path);
            var equal = actual.SequenceEqual(expected);
            string? expectedPath = null, actualPath = null;
            if (!equal)
            {
                Directory.CreateDirectory(yamlArtifacts);
                var leaf = entry.Id + "-" + phase;
                expectedPath = Path.Combine(yamlArtifacts, leaf + "-expected" + entry.Extension);
                actualPath = Path.Combine(yamlArtifacts, leaf + "-actual" + entry.Extension);
                File.WriteAllBytes(expectedPath, expected); File.WriteAllBytes(actualPath, actual);
                File.WriteAllText(Path.Combine(yamlArtifacts, leaf + "-receipt.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    phase, id = entry.Id.ToString(), entry.Path, expectedPath, actualPath,
                    expectedSha = Sha(expected), actualSha = Sha(actual), expectedLength = expected.Length, actualLength = actual.Length,
                    history = history.History, generation = authoring.Find(entry.Id)?.Generation,
                }));
            }
            checks.Add(new { trigger = "Exact native YAML", phase, id = entry.Id.ToString(), equal,
                expectedSha = Sha(expected), actualSha = Sha(actual), expectedPath, actualPath });
            Require(equal, "Native inheritance bytes changed at " + phase + "; retained expected/actual YAML: " + expectedPath + " / " + actualPath);
        }
        void BaseUnchanged()
        {
            Require(ReferenceEquals(workspace, currentWorkspace) && ReferenceEquals(sessionHistory, history)
                && ReferenceEquals(authoring.Find(source.Id), baseState) && baseState.IsPublished
                && ReferenceEquals(baseState.Document, baseOwner) && baseOwner.EditRevision == baseRevision,
                "Native inheritance replaced or edited the original canonical base owner.");
            Require(history.History.Entries.Take(baseline).Select(entry => entry.Id).SequenceEqual(prefix)
                && history.History.Entries.Take(baseline).All(entry => entry.IsDone), "Inheritance changed original applied history.");
        }
        Guid Transaction(int position, string title, EditorAuthoringState participant)
        {
            Require(history.History.Position == position + 1 && history.History.Entries[position].Name == title,
                "The actual inheritance command must publish exactly one named native transaction.");
            var actual = history.Stack.RetrieveAllTransactions().ElementAt(position);
            Require(history.GetParticipants(actual).Contains(participant), "The native journal did not retain its exact child generation.");
            var id = history.History.Entries[position].Id; ownedTransactions.Add(id); return id;
        }
        void Replay(Guid id, bool redo, int before)
        {
            var transactions = history.Stack.RetrieveAllTransactions().ToArray();
            var selector = redo ? history.Stack.NextRedoTransaction : history.Stack.NextUndoTransaction;
            Require(history.History.Position == before && selector != null
                && transactions.Zip(history.History.Entries).Any(pair => ReferenceEquals(pair.First, selector) && pair.Second.Id == id),
                "Inheritance replay cannot redirect a different native transaction.");
            if (redo) history.Redo(); else history.Undo();
            Require(history.History.Position == before + (redo ? 1 : -1)
                && history.History.Entries.Any(entry => entry.Id == id && entry.IsDone == redo), "Native inheritance replay lost transaction identity.");
            BaseUnchanged();
        }
        Asset Read(AssetEntry entry) => AssetFileSerializer.Load<Asset>(entry.Path,
            assetNamespace: currentWorkspace.Package.Container?.AssetNamespace ?? currentWorkspace.Package.AssetNamespace).Asset;
        void Links(Asset asset, AssetId id, bool archetype, bool replaces)
        {
            Require(asset.Id == id && asset.GetType() == typeof(ProceduralModelAsset)
                && (archetype ? asset.Archetype?.Id == source.Id : asset.Archetype == null)
                && (replaces ? asset.Replaces?.Id == source.Id : asset.Replaces == null),
                "Native inheritance ID/type/archetype/replacement references differ from the actual base.");
        }
        try
        {
            var originalTransactions = history.Stack.RetrieveAllTransactions().ToArray();
            baseOwner.Save(); BaseUnchanged();
            Require(!baseOwner.IsDirty && originalTransactions.SequenceEqual(history.Stack.RetrieveAllTransactions()),
                "Clean serializer baseline changed base state or native history.");
            canonicalBaseBytes = File.ReadAllBytes(source.Path);
            checks.Add(new { trigger = "Native base serializer baseline", sourceSha = Sha(sourceBytes), canonicalSha = Sha(canonicalBaseBytes), baseState.Generation });
            foreach (var replacing in new[] { false, true })
            {
                Require(history.History.Position == baseline && AssetTemplateNativeIds().SequenceEqual(baselineIds), "A previous variant did not restore native membership.");
                if (replacing) CreateReplacingAsset(source); else CreateDerivedAsset(source);
                BaseUnchanged();
                var childEntry = currentWorkspace.Assets.Single(entry => !baselineIds.Contains(entry.Id.ToString()));
                var childState = authoring.Find(childEntry.Id) ?? throw new InvalidOperationException("Native child publication has no canonical owner.");
                var child = (IAssetTagDocument)childState.Document;
                var childGraph = ((IEditorAssetGraphDocument)child).AuthoringGraph;
                var baseGraph = ((IEditorAssetGraphDocument)baseOwner).AuthoringGraph;
                children.Add((childEntry, child, childState));
                using var childLease = childState.AcquireView();
                var created = Transaction(baseline, replacing ? "Create replacing asset" : "Create derived asset", childState);
                Require(childEntry.Id != source.Id && childState.IsPublished && childEntry.Path != source.Path,
                    "Derived/replacing publication reused its base ID/path/generation.");
                Links(child.AuthoredAsset, childEntry.Id, true, replacing);
                var publishedItem = currentWorkspace.FindAsset(childEntry.Id);
                Require(ReferenceEquals(childGraph.AuthoredItem.Package, publishedItem.Package)
                    && childGraph.AuthoredItem.Location == publishedItem.Location
                    && ReferenceEquals(childGraph.RootNode.Retrieve(), child.AuthoredAsset),
                    "The created native child graph lacks its actual published package/namespace identity before Save.");
                child.Save(); var linkedBytes = File.ReadAllBytes(childEntry.Path); Links(Read(childEntry), childEntry.Id, true, replacing);
                child.Save(); Bytes(childEntry, linkedBytes, "repeated-linked-save");
                var linkRevision = child.EditRevision;
                var staleEntry = childEntry with { Path = childEntry.Path + ".stale" };
                Require(!CanClearAssetLink(staleEntry, true), "A stale captured child path remained eligible for link clearing.");
                ClearAssetLink(staleEntry, true);
                var attributes = File.GetAttributes(childEntry.Path);
                try
                {
                    File.SetAttributes(childEntry.Path, attributes | FileAttributes.ReadOnly);
                    Require(!CanClearAssetLink(childEntry, true), "A read-only native child file remained eligible for link clearing.");
                    ClearAssetLink(childEntry, true);
                    Bytes(childEntry, linkedBytes, "rejected-stale-readonly");
                    Require(child.EditRevision == linkRevision && history.History.Position == baseline + 1, "Rejected stale/read-only link commands changed authored state, history or native bytes.");
                    Links(child.AuthoredAsset, childEntry.Id, true, replacing);
                }
                finally { File.SetAttributes(childEntry.Path, attributes); }
                checks.Add(new { trigger = "Rejected stale-path and read-only native link commands", childId = childEntry.Id.ToString(), linkRevision });
                foreach (var archetype in replacing ? new[] { false, true } : new[] { true })
                {
                    Require(CanClearAssetLink(childEntry, archetype), "The native child link cannot be cleared.");
                    ClearAssetLink(childEntry, archetype);
                    var cleared = Transaction(baseline + 1, archetype ? "Clear archetype" : "Clear replaces", childState);
                    Links(child.AuthoredAsset, childEntry.Id, !archetype, replacing && archetype);
                    Require(archetype ? childGraph.Archetype == null : ReferenceEquals(childGraph.Archetype, baseGraph),
                        "Native clear-link command did not preserve its correct live base-graph registration.");
                    child.Save(); var clearedBytes = File.ReadAllBytes(childEntry.Path);
                    Links(Read(childEntry), childEntry.Id, !archetype, replacing && archetype);
                    // Clearing an absent link is the actual guarded no-op, not a synthetic journal operation.
                    ClearAssetLink(childEntry, archetype);
                    Require(history.History.Position == baseline + 2 && history.History.Entries[baseline + 1].Id == cleared,
                        "Clearing an already absent link added a native transaction.");
                    Replay(cleared, false, baseline + 2);
                    Require(ReferenceEquals(childGraph.Archetype, baseGraph) && ReferenceEquals(childGraph.Container.TryGetGraph(source.Id), baseGraph),
                        "Native link Undo did not restore the exact retained canonical base graph.");
                    child.Save(); Links(Read(childEntry), childEntry.Id, true, replacing);
                    Bytes(childEntry, linkedBytes, archetype ? "archetype-undo-save" : "replaces-undo-save");
                    Replay(cleared, true, baseline + 1); child.Save(); Links(Read(childEntry), childEntry.Id, !archetype, replacing && archetype);
                    Bytes(childEntry, clearedBytes, archetype ? "archetype-redo-save" : "replaces-redo-save");
                    Replay(cleared, false, baseline + 2); child.Save();
                    Bytes(childEntry, linkedBytes, archetype ? "archetype-final-undo-save" : "replaces-final-undo-save");
                    checks.Add(new { replacing, archetype, created, cleared, childId = childEntry.Id.ToString(), childState.Generation,
                        linkedSha = Sha(linkedBytes), clearedSha = Sha(clearedBytes) });
                }
                Replay(created, false, baseline + 1);
                Require(AssetTemplateNativeIds().SequenceEqual(baselineIds) && !File.Exists(childEntry.Path), "Creation Undo retained native membership or owned files.");
                Replay(created, true, baseline + 0);
                Require(ReferenceEquals(authoring.Find(childEntry.Id), childState) && childState.IsPublished,
                    "Creation Redo replaced its canonical child generation.");
                Links(Read(childEntry), childEntry.Id, true, replacing);
                Bytes(childEntry, linkedBytes, "creation-redo");
                Replay(created, false, baseline + 1);
                Require(!File.Exists(childEntry.Path) && AssetTemplateNativeIds().SequenceEqual(baselineIds), "Final creation Undo did not remove exact native child.");
                Bytes(source, canonicalBaseBytes, replacing ? "replacing-base-unchanged" : "derived-base-unchanged");
            }
            AssetInheritanceParityEvidence = new { coverage = "Actual native derived/replacing creation and guarded clear-link services, canonical graph/history and independent YAML; no UI gesture or preview claim.",
                sourceId = source.Id.ToString(), baseline, checks };
        }
        catch (Exception error) { failure = error; }
        finally
        {
            var cleanup = new List<Exception>();
            void Retire(Action action) { try { action(); } catch (Exception error) { cleanup.Add(error); } }
            Retire(() =>
            {
                while (history.History.Position > baseline)
                {
                    var top = history.History.Entries[history.History.Position - 1];
                    Require(ownedTransactions.Contains(top.Id), "Inheritance cleanup cannot undo another owner's transaction.");
                    Replay(top.Id, false, history.History.Position);
                }
                Require(AssetTemplateNativeIds().SequenceEqual(baselineIds) && children.All(child => !File.Exists(child.Entry.Path)),
                    "Inheritance cleanup retained native children or their files.");
            });
            foreach (var view in documents.Entries.Where(entry => !originalViews.Contains(entry)).ToArray())
                Retire(() => { Require(!view.Document.IsDirty, "Inheritance cleanup cannot close a dirty newly opened view."); CloseDocument(view); });
            Retire(() => RestoreCreationSelection(selection));
            Retire(() => { if (originalActive != null && documents.Entries.Contains(originalActive) && !ReferenceEquals(documents.Active, originalActive)) ActivateDocument(originalActive); });
            if (cleanup.Count != 0) { if (failure != null) cleanup.Insert(0, failure); failure = new AggregateException("Native inheritance verification and cleanup failed.", cleanup); }
        }
        AssetInheritanceParityEvidence = new { coverage = "Actual native derived/replacing creation and guarded clear-link services, canonical graph/history and independent YAML; no UI gesture, importer or compiled preview claim.",
            sourceId = source.Id.ToString(), baseline, checks, currentHistory = history.History, packageDirty = currentWorkspace.HasPackageChanges,
            children = children.Select(child => new { id = child.Entry.Id.ToString(), child.Entry.Path, child.State.Generation,
                published = child.State.IsPublished, fileExists = File.Exists(child.Entry.Path) }).ToArray(), failure = failure?.ToString() };
        if (failure != null) throw failure;
        // The production final Undo dirties real package metadata. Keep the canonical
        // base generation alive until actual SaveAllDocuments publication acknowledges.
        assetInheritanceParitySave = new(() =>
        {
            BaseUnchanged();
            Require(history.History.Position == baseline && AssetTemplateNativeIds().SequenceEqual(baselineIds)
                && children.All(child => !File.Exists(child.Entry.Path)), "Final native save changed restored inheritance membership/history/files.");
            Require(canonicalBaseBytes != null, "Final native save lost its certified baseline.");
            Bytes(source, canonicalBaseBytes, "final-package-save-base");
            Require(!currentWorkspace.HasPackageChanges && !baseOwner.IsDirty,
                "Final native save did not restore clean package/base metadata and exact canonical base bytes.");
            AssetInheritanceParityEvidence = new { coverage = "Actual native derived/replacing creation and guarded clear-link services, canonical graph/history and independent YAML; actual asynchronous final package save. No UI gesture, importer or compiled preview claim.",
                sourceId = source.Id.ToString(), baseline, checks, currentHistory = history.History, finalNativeSaveCompleted = true,
                packageDirty = currentWorkspace.HasPackageChanges, canonicalBaseSha = Sha(canonicalBaseBytes),
                children = children.Select(child => new { id = child.Entry.Id.ToString(), child.Entry.Path, child.State.Generation,
                    published = child.State.IsPublished, fileExists = File.Exists(child.Entry.Path) }).ToArray() };
        }, baseState.AcquireView());
    }
}
