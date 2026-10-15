using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsEditableAssetPackage(Package package) => !package.IsReadOnly && package.State >= PackageState.AssetsReady;
    private bool IsCurrentEditableAssetPackage(Package package) => workspace != null && workspace.Session.Packages.Contains(package)
        && ReferenceEquals(package.Session, workspace.Session) && IsEditableAssetPackage(package);
    private AssetCreationPanel? ownedAssetCreation;
    private AssetCreationPanel CreateAssetCreationPanel(string identity) => new(identity)
    { NativeMessageRenderer = (id, text) => DrawSelectableText(id, text), NativeTemplateRowRenderer = PaintAssetTemplateRow,
        RecordControl = id => { if (Controls.Enabled) Controls.Record(id); },
        RequestReferencePicker = RequestAssetTemplateReference, RetireReferenceLease = RetireTemplateReferenceLease };
    private AssetCreationPanel AssetCreation => pickerCreationScope is { } scope
        ? (scope.Panel ??= CreateAssetCreationPanel("PickerCreation/" + scope.Identity))
        : (ownedAssetCreation ??= CreateAssetCreationPanel("AssetCreation"));
    private bool CanAddAsset => workspace != null && authoring != null && sessionHistory != null && projectLoading == null
        && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating && resourceCopyRequest == null
        && !RecoveryTransitionBusy && !editorExtensionsStopping && !closeRequested && !closePromptOpen && closingDocument == null
        && !executingResourceCopies.Any(request => request.Authority.Canceled) && retiredPickerCreationCertificates.Count == 0 && PickerCreationCurrent && AssetCreationBrowser.CaptureNavigationScope() is { EntireWorkspace: false, Folders.Length: 1 }
        && AssetCreationBrowser.SelectedFolder is { } folder && IsCurrentEditableAssetPackage(folder.Package);
    private void RequestAssetCreation()
    {
        if (!CanAddAsset) return;
        documents.CommitAll();
        var owner = workspace!;
        var folder = AssetCreationBrowser.SelectedFolder;
        var package = folder?.Package ?? owner.Package;
        var parent = folder?.RelativePath ?? "";
        AssetCreation.Open(owner.Session, (choice, name, selectSource, reference, sourceNamespace) =>
        {
            var sameOwner = ReferenceEquals(workspace, owner);
            var eligible = CanAddAsset;
            var packageCurrent = IsCurrentEditableAssetPackage(package);
            if (!sameOwner || !eligible || !packageCurrent)
            {
                RecordAssetTemplateTargetGuard(owner, package, folder, choice, name, sameOwner, eligible, packageCurrent);
                throw new InvalidOperationException("The asset creation target is no longer available.");
            }
            ValidateDroppedAssetTarget(owner, folder!);
            var target = ResolveAssetTemplateLocation(folder!, choice.AssetType);
            if (choice.NativeParameters is { } prepared && (!ReferenceEquals(prepared.Parameters.Package, target.Package)
                || prepared.Parameters.TargetLocation.ToString() != target.RelativePath))
                throw new InvalidOperationException("The asset creation target changed.");
            if (choice.Adapter != null) { CreateRegisteredTemplateAsset(target.Package, target.RelativePath, choice, name, sourceNamespace, selectSource); return; }
            if (choice.Template?.RequireName == false) name = choice.Template.DefaultOutputName ?? choice.Template.AssetTypeName;
            if (choice.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset))
            { CreateScriptTemplateAsset(target.Package, target, choice, name, sourceNamespace); return; }
            if (selectSource) RequestFactoryAssetSources(target.Package, target.RelativePath, choice, name);
            else CreateFactoryAsset(target.Package, target.RelativePath, choice, name, reference: reference);
        }, package.Container is SolutionProject, folder?.IsCode == true, AssetTemplateNamespace(folder!), template => sessionHistory?.Extensions?.AcquireAssetTemplateAdapter(template), package, parent,
            AssetCreationBrowser.SelectedEntries.Select(entry =>
            {
                var item = workspace.FindAsset(entry.Id); var asset = authoring?.Find(item.Id)?.Document is IAssetTagDocument canonical ? canonical.AuthoredAsset : item.Asset;
                return item.Clone(true, newAsset: AssetCloner.Clone(asset));
            }).ToArray(), item =>
            {
                var asset = authoring?.Find(item.Id)?.Document is IAssetTagDocument canonical ? canonical.AuthoredAsset : item.Asset;
                return item.Clone(true, newAsset: AssetCloner.Clone(asset));
            }, type =>
            {
                var target = ResolveAssetTemplateLocation(folder!, type);
                return (target.Package, target.RelativePath, AssetTemplateNamespace(target));
            }, choice => SuggestAssetTemplateName(owner, folder!, choice));
    }
    private void DrawAssetCreationDialog() => (pickerCreationScope?.Panel ?? ownedAssetCreation)?.Draw(Execute);
    private void ClearAssetCreationState()
    {
        while (pickerCreationScope != null) CancelPickerAssetCreation();
        ownedAssetCreation?.Clear();
    }
    private AssetId CreateFactoryAsset(Package package, string parent, AssetCreationPanel.Choice choice, string name, UFile? source = null, AssetId? reference = null, Asset? preparedValue = null)
    {
        var prepared = PrepareFactoryAsset(package, parent, choice, name, source, reference, preparedValue);
        RequestFactorySourcePublication(package, [prepared], "Create asset '" + name + "'", () =>
        {
            CreatePreparedAsset(package, prepared.Item, prepared.Path, "Create asset '" + name + "'");
            SelectCreatedAssetBatch([prepared.Item.Id]);
        });
        return prepared.Item.Id;
    }
    private PreparedFactoryAsset PrepareFactoryAsset(Package package, string parent, AssetCreationPanel.Choice choice, string name,
        UFile? source = null, AssetId? reference = null, Asset? preparedValue = null, ISet<string>? reserved = null)
    {
        if (!IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The asset creation package is no longer available.");
        documents.CommitAll();
        if (!IsValidAssetLeafName(name)) throw new ArgumentException("Invalid asset name.");
        var asset = preparedValue ?? CreateAssetTemplateValue(package, choice, reference);
        if (asset.GetType() != choice.AssetType || asset is SourceCodeAsset || asset is IProjectAsset && asset is not Stride.Assets.Scripts.VisualScriptAsset)
            throw new InvalidOperationException("This asset requires its source template workflow.");
        var sourceFolder = asset is IProjectAsset ? package.RootDirectory : package.GetDefaultAssetFolder();
        var prefix = parent.Length == 0 ? "" : parent.TrimEnd('/') + "/";
        AssetItem Candidate(string leaf)
        {
            var url = new UFile(prefix + leaf);
            return new AssetItem(package.Container?.Qualify(url) ?? url, asset) { SourceFolder = sourceFolder };
        }
        string CandidatePath(string leaf)
        {
            var folder = sourceFolder;
            var absoluteFolder = folder.IsAbsolute ? folder : UPath.Combine(package.RootDirectory, folder);
            return UPath.Combine(absoluteFolder, new UFile(prefix + leaf + AssetRegistry.GetDefaultExtension(asset.GetType()))).ToOSPath();
        }
        bool Occupied(UFile leaf)
        {
            var candidate = Candidate(leaf.ToString());
            return reserved?.Contains(candidate.Location.ToString()) == true || package.Assets.Any(existing => string.Equals(existing.Location, candidate.Location, StringComparison.OrdinalIgnoreCase))
                || File.Exists(CandidatePath(leaf.ToString())) || Directory.Exists(CandidatePath(leaf.ToString())) || authoring!.Find(CandidatePath(leaf.ToString())) != null;
        }
        var item = Candidate(NamingHelper.ComputeNewName(name, Occupied, "{0}_{1}"));
        var path = CandidatePath(item.Location.GetFileName());
        if (source != null)
        {
            if (asset is not IAssetWithSource imported) throw new InvalidOperationException("This template requires its specialized source workflow.");
            imported.Source = new UFile(Path.GetRelativePath(Path.GetDirectoryName(path)!, source.ToOSPath()).Replace('\\', '/'));
        }
        if (preparedValue != null)
            foreach (var link in Stride.Core.Assets.Analysis.AssetReferenceAnalysis.Visit(asset))
                if (link.Reference is UPath original && original.IsAbsolute && link.Path.GetCustomAttribute<UPathAttribute>()?.RelativeTo != UPathRelativeTo.None)
                    link.UpdateReference(null, Path.GetRelativePath(Path.GetDirectoryName(path)!, original.ToOSPath()).Replace('\\', '/'));
        reserved?.Add(item.Location.ToString());
        return new(item, path);
    }
    private void CreatePreparedAsset(Package package, AssetItem item, string path, string title)
    {
        ValidatePickerCreationScope();
        if (!IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The asset creation package is no longer available.");
        if (!sessionHistory!.IsCapturingOperations)
        {
            var before = CaptureCreationSelection();
            try
            {
                using var capture = sessionHistory.CaptureOperationsAtomic(() => CreatePreparedAsset(package, item, path, title));
                capture.Publish(sessionHistory, title);
            }
            catch (Exception failure) { RestoreFailedCreationSelection(before, failure); throw; }
            return;
        }
        var asset = item.Asset;
        IAssetReferenceDocument created = asset switch
        {
            SceneAsset => new SceneDocument(path, new AssetLoadResult<Asset>(asset, null, false, item.YamlMetadata), sessionHistory!, false),
            PrefabAsset prefab => new PrefabDocument(path, prefab, item.YamlMetadata, sessionHistory!),
            ProceduralModelAsset procedural => new ProceduralAssetDocument(path,
                new AssetLoadResult<ProceduralModelAsset>(procedural, null, false, item.YamlMetadata), [], sessionHistory!, false),
            Stride.UI.Assets.UIAssetBase => new UIDocument(path, item, sessionHistory!),
            Stride.Assets.Scripts.VisualScriptAsset => new VisualScriptDocument(path, item, sessionHistory!),
            _ => new AssetMetadataDocument(path, item, sessionHistory!),
        };
        EditorAuthoringState? state = null;
        try
        {
            if (!created.CanSave) throw new InvalidOperationException("This asset can't be created until its types are available.");
            state = created switch
            {
                SceneDocument scene => authoring!.RegisterUnpublished(scene, item.Id),
                PrefabDocument prefab => authoring!.RegisterUnpublished(prefab, item.Id),
                ProceduralAssetDocument procedural => authoring!.RegisterUnpublished(procedural, item.Id),
                UIDocument ui => authoring!.RegisterUnpublished(ui, item.Id),
                VisualScriptDocument script => authoring!.RegisterUnpublished(script, item.Id),
                AssetMetadataDocument metadataDocument => authoring!.RegisterUnpublished(metadataDocument, item.Id),
                _ => throw new InvalidOperationException("This asset has no supported authored document."),
            };
            using var retention = state.AcquireView();
            {
                var directories = new OwnedSourceDirectoriesOperation(package.RootDirectory.ToOSPath());
                try { directories.Ensure(Path.GetDirectoryName(path)!); }
                catch { directories.CancelInitial(); throw; }
                if (directories.HasDirectories)
                {
                    try { sessionHistory!.PushOperation(directories, state); }
                    catch { directories.CancelInitial(); throw; }
                }
            }
            var metadata = PackageMetadataRevision.Prepare(sessionHistory, workspace!, [package]);
            var operation = new AssetCreationOperation(this, package, item, state, metadata);
            using var transaction = sessionHistory!.CreateTransaction(title);
            operation.ApplyInitial();
            try { sessionHistory.PushOperation(operation, [state, .. metadata.Select(change => change.State)]); }
            catch { operation.CancelInitial(); throw; }
            if (created is VisualScriptDocument visual)
            {
                var project = package.Container as SolutionProject ?? throw new InvalidOperationException("The visual script requires an actual project.");
                var projectDocument = GetProjectBuildSettingsDocument(project, sessionHistory!);
                projectDocument.ValidateProjectFileBaseline(); projectDocument.ChangeGeneratedAssetMembership(item, true);
                InitializeVisualScriptDocument(visual); UpdateVisualScriptGeneratedOutput(visual);
            }
            var browser = AssetCreationBrowser;
            browser.RefreshEntries(workspace!);
            if (ReferenceEquals(browser, assetBrowser))
            {
                if (browser.RevealAsset(workspace!, item.Id) is { } entry) selectedAssetPath = entry.Path;
                ActivateAssetPropertyContext();
            }
        }
        catch
        {
            if (state == null) created.Dispose();
            else if (!state.IsPublished) authoring!.ReleaseIfUnused(state);
            throw;
        }
    }
    private static bool IsValidAssetLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ')) return false;
        var device = name.Split('.')[0];
        return !device.Equals("CON", StringComparison.OrdinalIgnoreCase) && !device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !device.Equals("AUX", StringComparison.OrdinalIgnoreCase) && !device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && device[3] is >= '1' and <= '9');
    }
}
