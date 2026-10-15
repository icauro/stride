using System.Security.Cryptography;
using System.Text;
using Stride.Assets.Materials;
using Stride.Assets.Models;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetCrossVolumeRelocationParityRun? crossVolumeRelocationParity;
    internal AssetCrossVolumeRelocationParityRun BeginAssetCrossVolumeRelocationParityVerification(string artifacts)
    {
        if (crossVolumeRelocationParity != null || !Controls.Enabled || !args.Contains("--ui-test") || workspace == null
            || sessionHistory == null || authoring == null || !CanStartAssetEdit || IsPackageMetadataSaving)
            throw new InvalidOperationException("Cross-volume relocation requires an idle retained disposable native workspace.");
        return crossVolumeRelocationParity = new(this, Path.GetFullPath(artifacts));
    }
    internal bool StepAssetCrossVolumeRelocationParityVerification(AssetCrossVolumeRelocationParityRun run)
    {
        if (!ReferenceEquals(crossVolumeRelocationParity, run)) throw new InvalidOperationException("The retained relocation fixture changed.");
        return run.Step();
    }
    internal bool TryRetireAssetCrossVolumeRelocationParityVerification()
    {
        var run = crossVolumeRelocationParity;
        if (run == null) return true;
        if (IsPackageMetadataSaving || run.SaveRequested && !run.SaveSettled) return false;
        if (!run.TryRetire()) return false;
        crossVolumeRelocationParity = null; return true;
    }

    internal sealed class AssetCrossVolumeRelocationParityRun
    {
        private readonly EditorGame game;
        private readonly ProjectWorkspace workspace;
        private readonly EditorSessionHistory history;
        private readonly Package source, destination;
        private readonly string artifacts, sourceProject, destinationProject, sourcePath, destinationPath, modelPath;
        private readonly AssetId materialId, modelId;
        private readonly IAssetReferenceDocument material, model;
        private readonly EditorAuthoringState materialState, modelState;
        private readonly List<IDisposable> leases = [];
        private readonly EditorDocumentEntry[] initialViews;
        private readonly EditorDocumentEntry? initialActive;
        private readonly CreationSelection initialSelection;
        private readonly List<object> checks = [];
        private readonly PackageLoadParameters load;
        private int stage, baseline;
        private Guid[] prefix = [];
        private Guid? transaction;
        private byte[] materialBytes = [], modelBytes = [], movedMaterialBytes = [], movedModelBytes = [];
        private string originalUrl = "", movedUrl = "";
        private uint sourceVolume, destinationVolume;
        private Exception? saveError;
        private bool released, baselineCaptured, retirementSaveStarted;
        private byte[]? ownedCollisionBytes, ownedExternalBytes;
        internal bool SaveRequested { get; private set; }
        internal bool SaveSettled { get; private set; }
        internal object Evidence => new { stage, baseline, transaction, sourceProject, destinationProject, sourcePath, destinationPath,
            materialId = materialId.ToString(), modelId = modelId.ToString(), sourceVolume, destinationVolume,
            materialGeneration = materialState.Generation, modelGeneration = modelState.Generation, checks,
            SaveRequested, SaveSettled, saveError = saveError?.ToString(),
            coverage = "Actual cross-volume cross-package authored relocation, incoming native material references/root declarations, canonical shared history, Save and independent native project/package reload. Fixture material/model types; no UI gesture or every asset type claim." };
        internal AssetCrossVolumeRelocationParityRun(EditorGame game, string artifacts)
        {
            this.game = game; this.artifacts = artifacts; workspace = game.workspace!; history = game.sessionHistory!;
            initialViews = game.documents.Entries.ToArray(); initialActive = game.documents.Active;
            initialSelection = game.CaptureCreationSelection(game.assetBrowser);
            var token = Guid.NewGuid().ToString("N");
            var sourceRoot = Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "authored-volume-" + token);
            var destinationRoot = Path.Combine(artifacts, "authored-volume-" + token);
            if (Directory.Exists(sourceRoot) || Directory.Exists(destinationRoot)) throw new IOException("Private authored transfer fixture already exists.");
            var sourceName = "VolumeSource" + token; var destinationName = "VolumeDestination" + token;
            var target = new MaterialAsset(); var incoming = new ProceduralModelAsset { Type = new CubeProceduralModel() };
            ((CubeProceduralModel)incoming.Type).MaterialInstance.Material = AttachedReferenceManager.CreateProxyObject<Material>(target.Id, "/" + sourceName + "/Material");
            materialId = target.Id; modelId = incoming.Id;
            sourceProject = Arrange(sourceRoot, sourceName, [new("Material", target), new("Incoming", incoming)], materialId);
            destinationProject = Arrange(destinationRoot, destinationName, [], materialId);
            load = new PackageLoadParameters { LoadMissingDependencies = false, LoadAssemblyReferences = false, AutoCompileProjects = false,
                AutoLoadTemporaryAssets = true, ConvertUPathToAbsolute = false, TemporaryAssetsInMsbuild = false };
            var log = new LoggerResult();
            var sourceContainer = workspace.Session.AddExistingProject(new UFile(sourceProject), log, load);
            var destinationContainer = workspace.Session.AddExistingProject(new UFile(destinationProject), log, load);
            source = sourceContainer.Package; destination = destinationContainer.Package;
            Directory.CreateDirectory(artifacts);
            var loadReceipt = new { action = "Actual native fixture asset discovery", sourceProject, destinationProject,
                load.AutoLoadTemporaryAssets, load.TemporaryAssetsInMsbuild, load.LoadAssemblyReferences, load.AutoCompileProjects,
                expectedMaterialId = materialId.ToString(), expectedModelId = modelId.ToString(), nativeLog = log.ToText(),
                packages = new[] { source, destination }.Select(owner => new { owner.FullPath, owner.State, owner.IsReadOnly,
                    assets = owner.Assets.Select(item => new { id = item.Id.ToString(), type = item.Asset.GetType().FullName,
                        location = item.Location.ToString(), path = item.FullPath.ToOSPath() }).ToArray() }).ToArray() };
            File.WriteAllText(Path.Combine(artifacts, "authored-cross-volume-load.json"), System.Text.Json.JsonSerializer.Serialize(loadReceipt));
            checks.Add(loadReceipt);
            if (log.HasErrors) throw new InvalidOperationException("Native writable fixture load failed: " + log.ToText());
            Require(sourceContainer is SolutionProject && destinationContainer is SolutionProject && !source.IsReadOnly && !destination.IsReadOnly
                && source.State >= PackageState.AssetsReady && destination.State >= PackageState.AssetsReady,
                "Actual project bootstrap did not produce two writable AssetsReady SolutionProject owners.");

            workspace.RefreshAssets();
            sourcePath = workspace.FindAsset(materialId).FullPath.ToOSPath(); modelPath = workspace.FindAsset(modelId).FullPath.ToOSPath();
            destinationPath = Path.Combine(destination.GetDefaultAssetFolder().IsAbsolute ? destination.GetDefaultAssetFolder().ToOSPath()
                : Path.Combine(destination.RootDirectory.ToOSPath(), destination.GetDefaultAssetFolder().ToOSPath()), "MovedMaterial.sdmat");
            material = (IAssetReferenceDocument)game.GetAssetTagDocument(workspace.Assets.Single(entry => entry.Id == materialId), false, false);
            model = (IAssetReferenceDocument)game.GetAssetTagDocument(workspace.Assets.Single(entry => entry.Id == modelId), false, false);
            materialState = game.authoring!.Find(materialId)!; modelState = game.authoring.Find(modelId)!;
            originalUrl = sourceContainer.Qualify(new UFile("Material")).ToString(); movedUrl = destinationContainer.Qualify(new UFile("MovedMaterial")).ToString();
            checks.Add(new { action = "Actual writable native project bootstrap", sourceProject, destinationProject,
                sourceType = sourceContainer.GetType().FullName, destinationType = destinationContainer.GetType().FullName, originalUrl, movedUrl });
            try { leases.Add(materialState.AcquireView()); leases.Add(modelState.AcquireView()); }
            catch { foreach (var lease in leases) lease.Dispose(); throw; }
        }
        private sealed record FixtureAsset(string Name, Asset Asset);
        private static string Arrange(string root, string name, FixtureAsset[] assets, AssetId rootId)
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            var project = Path.Combine(root, name + ".csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><StrideContainsAssetTypes>false</StrideContainsAssetTypes><StrideAssetNamespace>" + name + "</StrideAssetNamespace></PropertyGroup></Project>");
            var package = new Package(); package.Meta.Name = name; package.Meta.Version = new PackageVersion("1.0.0");
            package.AssetNamespace = name; package.AssetFolders.Add(new AssetFolder(new UDirectory("Assets")));
            package.RootAssets.Add(new AssetReference(rootId, new UFile("/" + (assets.Length != 0 ? name : name.Replace("VolumeDestination", "VolumeSource")) + "/Material")));
            foreach (var asset in assets) AssetFileSerializer.Save(Path.Combine(root, "Assets", asset.Name + AssetRegistry.GetDefaultExtension(asset.Asset.GetType())), asset.Asset, null, assetNamespace: name);
            AssetFileSerializer.Save(Path.ChangeExtension(project, ".sdpkg"), package, null); return project;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static uint Volume(string path)
        { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return WindowsSourceFileIdentity.Capture(stream).Volume; }
        private void Current()
        {
            Require(ReferenceEquals(game.workspace, workspace) && ReferenceEquals(game.sessionHistory, history)
                && ReferenceEquals(game.authoring!.Find(materialId), materialState) && ReferenceEquals(game.authoring.Find(modelId), modelState)
                && materialState.IsPublished && modelState.IsPublished && ReferenceEquals(materialState.Document, material) && ReferenceEquals(modelState.Document, model),
                "Authored transfer lost its actual workspace or canonical generations.");
        }
        private bool Ready => game.CanStartAssetEdit && !game.IsPackageMetadataSaving;
        private bool Save(int next)
        {
            if (!SaveRequested)
            {
                SaveRequested = true; SaveSettled = false; saveError = null;
                try { game.SaveAllDocuments(() => { Current(); SaveSettled = true; }, error => { saveError = error; SaveSettled = true; }); }
                catch (Exception error) { saveError = error; SaveSettled = true; }
            }
            if (!SaveSettled || game.IsPackageMetadataSaving) return false;
            if (saveError != null) throw saveError;
            SaveRequested = SaveSettled = false; stage = next; return true;
        }
        private AssetEntry Entry => workspace.Assets.Single(entry => entry.Id == materialId);
        private void State(bool moved, bool files = false)
        {
            Current(); var item = workspace.FindAsset(materialId); var package = moved ? destination : source; var path = moved ? destinationPath : sourcePath;
            Require(ReferenceEquals(item.Package, package) && SamePath(item.FullPath.ToOSPath(), path)
                && SamePath(material.Path, path) && SamePath(materialState.CanonicalPath, path)
                && source.Assets.Count(asset => asset.Id == materialId) == (moved ? 0 : 1)
                && destination.Assets.Count(asset => asset.Id == materialId) == (moved ? 1 : 0), "Native package membership/path/registry identity differs after transfer.");
            var expected = moved ? movedUrl : originalUrl;
            var reference = AttachedReferenceManager.GetAttachedReference(((CubeProceduralModel)((ProceduralModelAsset)((IAssetTagDocument)model).AuthoredAsset).Type).MaterialInstance.Material);
            Require(reference != null && reference.Id == materialId && reference.Url == expected, "Incoming canonical model reference lost its target ID/qualified URL.");
            Require(new[] { source, destination }.All(owner => owner.RootAssets.Single(root => root.Id == materialId).Location.ToString() == expected),
                "Native package root references did not follow the exact moved target URL.");
            if (files) Require(File.Exists(path) && !File.Exists(moved ? sourcePath : destinationPath), "Actual owned cross-volume file publication differs from native membership.");
        }
        private void Reload(bool moved)
        {
            using var check = new PackageSession(); var log = new LoggerResult();
            var a = check.AddExistingProject(new UFile(sourceProject), log, load).Package;
            var b = check.AddExistingProject(new UFile(destinationProject), log, load).Package;
            var reloadReceipt = new { action = "Actual independent native discovery", moved,
                load.AutoLoadTemporaryAssets, load.TemporaryAssetsInMsbuild, log = log.ToText(),
                packages = new[] { a, b }.Select(owner => new { path = owner.FullPath.ToOSPath(), owner.State, owner.IsReadOnly,
                    assets = owner.Assets.Select(item => new { id = item.Id.ToString(), type = item.Asset.GetType().FullName,
                        location = item.Location.ToString(), path = item.FullPath.ToOSPath() }).ToArray() }).ToArray() };
            checks.Add(reloadReceipt);
            File.WriteAllText(Path.Combine(artifacts, "authored-cross-volume-reload-" + stage + ".json"), System.Text.Json.JsonSerializer.Serialize(reloadReceipt));
            Require(!log.HasErrors && a.Assets.Count(asset => asset.Id == materialId) == (moved ? 0 : 1)
                && b.Assets.Count(asset => asset.Id == materialId) == (moved ? 1 : 0), "Independent actual project/package reload lost moved native membership.");
            var asset = (ProceduralModelAsset)a.Assets.Find(modelId)!.Asset;
            var reference = AttachedReferenceManager.GetAttachedReference(((CubeProceduralModel)asset.Type).MaterialInstance.Material);
            Require(reference?.Id == materialId && reference.Url == (moved ? movedUrl : originalUrl)
                && new[] { a, b }.All(owner => owner.RootAssets.Single(root => root.Id == materialId).Location.ToString() == (moved ? movedUrl : originalUrl)),
                "Independent native reload lost incoming or root reference locations.");
            var incoming = workspace.Session.DependencyManager.CaptureDependencies(new Dictionary<AssetId, Asset>
                { [materialId] = ((IAssetTagDocument)material).AuthoredAsset, [modelId] = ((IAssetTagDocument)model).AuthoredAsset });
            Require(incoming.GetValueOrDefault(materialId)?.LinksIn.Any(link => link.Item.Id == modelId) == true, "The actual native dependency graph lost its incoming model link.");
            checks.Add(new { action = "Independent native project/package reload", moved, reference.Url });
        }
        private void Bytes(string path, byte[] expected, string phase)
        {
            var actual = File.ReadAllBytes(path);
            if (!actual.SequenceEqual(expected))
            {
                var root = Path.Combine(artifacts, "authored-cross-volume-yaml"); Directory.CreateDirectory(root);
                File.WriteAllBytes(Path.Combine(root, phase + "-expected" + Path.GetExtension(path)), expected);
                File.WriteAllBytes(Path.Combine(root, phase + "-actual" + Path.GetExtension(path)), actual);
                checks.Add(new { action = "Exact native YAML mismatch", phase, path,
                    expectedSha256 = Convert.ToHexString(SHA256.HashData(expected)), actualSha256 = Convert.ToHexString(SHA256.HashData(actual)) });
                File.WriteAllText(Path.Combine(root, phase + "-receipt.json"), System.Text.Json.JsonSerializer.Serialize(Evidence));
                throw new InvalidOperationException("Native authored transfer bytes differ at " + phase);
            }
        }
        internal bool Step()
        {
            Current();
            switch (stage)
            {
                case 0: Save(1); break;
                case 1:
                    if (!Ready) break;
                    baseline = history.History.Position; prefix = history.History.Entries.Take(baseline).Select(entry => entry.Id).ToArray();
                    materialBytes = File.ReadAllBytes(sourcePath); modelBytes = File.ReadAllBytes(modelPath); baselineCaptured = true;
                    sourceVolume = Volume(sourcePath);
                    var proof = Path.Combine(destination.RootDirectory.ToOSPath(), "volume-proof.bin");
                    WindowsOwnedAssetFileWriter.Write(proof, null, [2, 4, 6]); destinationVolume = Volume(proof);
                    WindowsOwnedAssetFileDeletion.Delete(proof, SHA256.HashData(new byte[] { 2, 4, 6 }), true);
                    Require(sourceVolume != destinationVolume, "Authored projects do not resolve to distinct actual Windows volumes.");
                    State(false, true); stage = 2; break;
                case 2:
                    if (!Ready) break;
                    var occupied = Encoding.UTF8.GetBytes("Actual independent destination owner");
                    WindowsOwnedAssetFileWriter.Write(destinationPath, null, occupied); ownedCollisionBytes = occupied;
                    var collision = false;
                    try { game.RelocateAsset(Entry, "MovedMaterial", destination); } catch (IOException) { collision = true; }
                    Require(collision && File.ReadAllBytes(destinationPath).SequenceEqual(occupied) && history.History.Position == baseline, "Occupied destination was accepted or altered native history/files.");
                    State(false); Bytes(sourcePath, materialBytes, "collision-material"); Bytes(modelPath, modelBytes, "collision-model");
                    WindowsOwnedAssetFileDeletion.Delete(destinationPath, SHA256.HashData(occupied), true); ownedCollisionBytes = null;
                    var changed = materialBytes.Concat(Encoding.UTF8.GetBytes("\n# Controlled external source change\n")).ToArray();
                    WindowsOwnedAssetFileWriter.Write(sourcePath, SHA256.HashData(materialBytes), changed); ownedExternalBytes = changed;
                    var stale = false;
                    try { game.RelocateAsset(Entry, "MovedMaterial", destination); } catch (IOException) { stale = true; }
                    Require(stale && history.History.Position == baseline && history.History.Entries.Take(baseline).Select(entry => entry.Id).SequenceEqual(prefix),
                        "Stale source did not reject and compensate the actual grouped native relocation.");
                    State(false, true); Bytes(sourcePath, changed, "rejected-external-source"); Bytes(modelPath, modelBytes, "stale-model");
                    WindowsOwnedAssetFileWriter.Write(sourcePath, SHA256.HashData(changed), materialBytes); ownedExternalBytes = null;
                    checks.Add(new { action = "Collision and actual stale-source publication compensation", sourceVolume, destinationVolume }); stage = 3; break;
                case 3: Save(4); break;
                case 4:
                    if (!Ready) break;
                    game.RelocateAsset(Entry, "MovedMaterial", destination); State(true, true);
                    Require(history.History.Position == baseline + 1, "Cross-package authored relocation did not publish one shared transaction.");
                    transaction = history.History.Entries[baseline].Id;
                    var participants = history.GetParticipants(history.Stack.RetrieveAllTransactions().ElementAt(baseline));
                    Require(participants.Contains(materialState) && participants.Contains(modelState)
                        && participants.Count(state => state.Document is PackageMetadataDocument) >= 2,
                        "Shared relocation journal lacks exact target/referencer/package metadata participants.");
                    Require(Volume(destinationPath) == destinationVolume, "Moved authored file did not reach the actual destination volume."); stage = 5; break;
                case 5: Save(6); break;
                case 6:
                    Reload(true); movedMaterialBytes = File.ReadAllBytes(destinationPath); movedModelBytes = File.ReadAllBytes(modelPath);
                    Replay(false); State(false, true); stage = 7; break;
                case 7: Save(8); break;
                case 8:
                    Reload(false); Bytes(sourcePath, materialBytes, "undo-material"); Bytes(modelPath, modelBytes, "undo-model");
                    Replay(true); State(true, true); stage = 9; break;
                case 9: Save(10); break;
                case 10:
                    Reload(true); Bytes(destinationPath, movedMaterialBytes, "redo-material"); Bytes(modelPath, movedModelBytes, "redo-model");
                    Replay(false); State(false, true); stage = 11; break;
                case 11: Save(12); break;
                case 12:
                    Reload(false); Bytes(sourcePath, materialBytes, "final-material"); Bytes(modelPath, modelBytes, "final-model");
                    Require(history.History.Position == baseline && history.History.Entries.Take(baseline).Select(entry => entry.Id).SequenceEqual(prefix)
                        && !workspace.HasPackageChanges && !material.IsDirty && !model.IsDirty, "Final authored transfer did not restore clean actual fixture/history baseline.");
                    checks.Add(new { action = "Final exact authored baseline restored", transaction, nativeSaveCompleted = true });
                    File.WriteAllText(Path.Combine(artifacts, "authored-cross-volume-result.json"), System.Text.Json.JsonSerializer.Serialize(Evidence)); return true;
            }
            return false;
        }
        private void Replay(bool redo)
        {
            var expected = redo ? baseline : baseline + 1;
            var nativeTransactions = history.Stack.RetrieveAllTransactions().ToArray();
            var selector = redo ? history.Stack.NextRedoTransaction : history.Stack.NextUndoTransaction;
            Require(history.History.Position == expected && history.History.Entries[baseline].Id == transaction
                && selector != null && nativeTransactions.Zip(history.History.Entries).Any(pair => ReferenceEquals(pair.First, selector) && pair.Second.Id == transaction),
                "Authored relocation replay cannot redirect another transaction.");
            checks.Add(new { action = "Exact native transaction selected", redo, expectedPosition = expected, transaction,
                participants = history.GetParticipants(selector!).Select(state => new { assetId = state.AssetId.ToString(), state.Generation, state.CanonicalPath }).ToArray() });
            if (redo) history.Redo(); else history.Undo();
            Require(history.History.Position == (redo ? baseline + 1 : baseline) && history.History.Entries[baseline].Id == transaction,
                "Authored relocation replay changed native transaction identity.");
        }
        internal bool TryRetire()
        {
            if (released) return true;
            if (game.IsPackageMetadataSaving || SaveRequested && !SaveSettled) return false;
            Current();
            if (ownedCollisionBytes != null)
            {
                WindowsOwnedAssetFileDeletion.Delete(destinationPath, SHA256.HashData(ownedCollisionBytes), true);
                ownedCollisionBytes = null;
            }
            if (ownedExternalBytes != null)
            {
                WindowsOwnedAssetFileWriter.Write(sourcePath, SHA256.HashData(ownedExternalBytes), materialBytes);
                ownedExternalBytes = null;
            }
            if (baselineCaptured)
            {
                Require(history.History.Entries.Take(baseline).Select(entry => entry.Id).SequenceEqual(prefix),
                    "Failed relocation cannot compensate another owner's history prefix.");
                if (history.History.Position == baseline + 1 && transaction != null) Replay(false);
                Require(history.History.Position == baseline, "Failed relocation retains an unknown applied transaction; its owners must remain retained.");
                State(false, true);
                if (!retirementSaveStarted)
                {
                    SaveRequested = SaveSettled = false; saveError = null; retirementSaveStarted = true;
                    Save(stage);
                }
                else if (SaveRequested) Save(stage);
                if (SaveRequested || game.IsPackageMetadataSaving) return false;
                if (saveError != null) throw new InvalidOperationException("Actual authored relocation cleanup save failed; fixture owners remain retained.", saveError);
                Bytes(sourcePath, materialBytes, "retirement-material"); Bytes(modelPath, modelBytes, "retirement-model");
                Require(!workspace.HasPackageChanges && !material.IsDirty && !model.IsDirty,
                    "Actual authored relocation cleanup did not acknowledge the clean native baseline.");
            }
            foreach (var entry in game.documents.Entries.Where(entry => !initialViews.Contains(entry)).ToArray())
                if (!entry.Document.IsDirty) game.CloseDocument(entry);
            game.RestoreCreationSelection(initialSelection);
            if (initialActive != null && game.documents.Entries.Contains(initialActive) && !ReferenceEquals(game.documents.Active, initialActive)) game.ActivateDocument(initialActive);
            foreach (var lease in leases) lease.Dispose(); released = true;
            // The real loaded project/package owners and their files remain owned
            // by the actual session/history until normal outer terminal disposal.
            return true;
        }
    }
}
