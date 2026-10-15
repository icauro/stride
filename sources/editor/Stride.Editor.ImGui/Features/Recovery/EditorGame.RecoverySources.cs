using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorDocumentEntry OpenRecoverySourcePresentation(EditorRecoveryOpenDocument descriptor)
    {
        if (IsUnownedReadonlyRecoverySource(descriptor)) return OpenUnownedReadonlyRecoverySource(descriptor);
        var generated = descriptor.GeneratedByAssetId != null;
        var proof = generated ? ValidateRecoveryGeneratedSourceMembership(descriptor) : ValidateRecoverySourceMembership(descriptor);
        var existing = documents.Find(descriptor.Path);
        if (existing != null)
        {
            if (existing.Document is not ScriptSourceDocument retained
                || retained.GetType().FullName != descriptor.Kind || retained.ProjectPath == null || !SamePath(retained.ProjectPath, proof.ProjectPath))
                throw new InvalidOperationException("The retained recovered source has another native project owner.");
            if (retained.GeneratedByAssetId?.ToString() != descriptor.GeneratedByAssetId
                || descriptor.ReadOnlyPresentation && retained.CanEditText)
                throw new InvalidOperationException("Recovered presentation cannot change the retained source's generated identity or editing permissions.");
            ValidateRecoverySourcePresentationOwner(existing, descriptor);
            return existing;
        }
        var source = GetCanonicalDocument(descriptor.Path, () =>
        {
            ScriptSourceDocument next = generated && !File.Exists(descriptor.Path)
                ? new ScriptSourceDocument(descriptor.Path, new AssetId(Guid.Parse(descriptor.GeneratedByAssetId!)), proof.ProjectPath, sessionHistory!)
                : descriptor.AssetId != null ? CreateScriptDocument(descriptor.Path)
                : new ScriptSourceDocument(descriptor.Path, readOnly: descriptor.ReadOnlyPresentation || generated, history: sessionHistory);
            if (!generated && descriptor.ReadOnlyPresentation && next.CanEditText)
            {
                next.Dispose();
                throw new InvalidOperationException("Recovered read-only source presentation no longer matches its current native source policy.");
            }
            if (generated)
            {
                try
                {
                    var owner = ResolveRecoveryGeneratedSourceProducer(descriptor);
                    try
                    {
                        next.BindGeneratedOutput(owner.Item.Id, proof.ProjectPath, owner.Item.Package.IsReadOnly, owner.Producer);
                        next.VerifyGeneratedOutput = VerifyVisualScriptGeneratedOutput;
                        if (!File.Exists(descriptor.Path))
                        {
                            if (owner.Producer.Document is not VisualScriptDocument visual
                                || visual.IsDirty && (!visual.DiskContentHash.IsEmpty || File.Exists(visual.Path)))
                                throw new InvalidOperationException("Restore the actual visual-script producer before reopening its missing generated projection.");
                            var snapshot = visual.CaptureCompilation(owner.Item);
                            next.RefreshGeneratedProjection(snapshot.Result.GeneratedSource
                                ?? throw new InvalidDataException("The native visual-script compiler returned no projection."), owner.Producer);
                        }
                    }
                    finally { owner.Producer.Registry.ReleaseIfUnused(owner.Producer); }
                }
                catch { next.Dispose(); throw; }
            }
            next.SetProjectOwnership(proof.ProjectPath);
            return next;
        });
        if (source is not ScriptSourceDocument native || native.GeneratedByAssetId?.ToString() != descriptor.GeneratedByAssetId
            || descriptor.ReadOnlyPresentation && native.CanEditText
            || native.GetType().FullName != descriptor.Kind || native.ProjectPath == null || !SamePath(native.ProjectPath, proof.ProjectPath))
            throw new InvalidOperationException("The recovered source has another retained native project owner.");
        var entry = documents.Add(native);
        try { ValidateRecoverySourcePresentationOwner(entry, descriptor); ActivateDocument(entry); }
        catch { documents.Remove(entry); throw; }
        return entry;
    }
    private static bool IsUnownedReadonlyRecoverySource(EditorRecoveryOpenDocument descriptor)
        => descriptor.Kind == typeof(ScriptSourceDocument).FullName && descriptor.ReadOnlyPresentation
            && descriptor.AssetId == null && descriptor.PackagePath == null && descriptor.Namespace == null
            && descriptor.GeneratedByAssetId == null && descriptor.SourceProjectPath == null;

    private void ValidateUnownedReadonlyRecoverySource(EditorRecoveryOpenDocument descriptor)
    {
        var current = workspace ?? throw new InvalidOperationException("No recovery project is loaded.");
        if (!IsUnownedReadonlyRecoverySource(descriptor) || !File.Exists(descriptor.Path))
            throw new InvalidOperationException("The recovered read-only source view has no existing file.");
        var path = Path.GetFullPath(descriptor.Path);
        if (current.AssetItems.Any(item => SamePath(item.FullPath.ToOSPath(), path)
            || item.Asset is IProjectFileGeneratorAsset && SamePath(item.GetGeneratedAbsolutePath().ToOSPath(), path)))
            throw new InvalidOperationException("The recovered read-only source view has a current native asset or generated producer owner.");
        if (authoring?.Find(path) is { } state)
        {
            if (state.IsDisposed || !state.IsPublished || state.AssetId != null
                || state.Document is not ScriptSourceDocument source || source.GetType() != typeof(ScriptSourceDocument)
                || source.ProjectPath != null || source.GeneratedByAssetId != null || source.CanEditText || source.CanSave || source.IsDirty)
                throw new InvalidOperationException("The retained read-only source view has another current ownership or editing policy.");
        }
    }

    private EditorDocumentEntry OpenUnownedReadonlyRecoverySource(EditorRecoveryOpenDocument descriptor)
    {
        ValidateUnownedReadonlyRecoverySource(descriptor);
        var source = GetCanonicalDocument(descriptor.Path,
            () => new ScriptSourceDocument(descriptor.Path, readOnly: true, history: sessionHistory));
        if (source is not ScriptSourceDocument native || native.GetType() != typeof(ScriptSourceDocument)
            || native.CanEditText || native.CanSave || native.IsDirty || native.ProjectPath != null || native.GeneratedByAssetId != null)
            throw new InvalidOperationException("The recovered read-only view cannot acquire source authoring permissions.");
        var entry = documents.Find(descriptor.Path);
        if (entry != null)
        {
            ValidateUnownedReadonlyRecoverySource(descriptor);
            ValidateRecoverySourcePresentationOwner(entry, descriptor);
            return entry;
        }
        entry = documents.Add(native);
        try
        {
            ValidateUnownedReadonlyRecoverySource(descriptor);
            ValidateRecoverySourcePresentationOwner(entry, descriptor);
            ActivateDocument(entry);
        }
        catch { documents.Remove(entry); throw; }
        return entry;
    }
    private void ValidateRecoverySourcePresentationOwner(EditorDocumentEntry entry, EditorRecoveryOpenDocument descriptor)
    {
        var state = entry.State;
        if (state == null || state.IsDisposed || !state.IsPublished || !ReferenceEquals(authoring?.Find(descriptor.Path), state)
            || !ReferenceEquals(state.Document, entry.Document) || !SamePath(state.CanonicalPath, descriptor.Path)
            || state.AssetId?.ToString() != descriptor.AssetId)
            throw new InvalidOperationException("The recovered source view does not retain its current canonical generation.");
        if (descriptor.GeneratedByAssetId != null)
        {
            _ = ValidateRecoveryGeneratedSourceMembership(descriptor);
            var generatedView = entry.Document as ScriptSourceDocument;
            var producer = authoring?.Find(new AssetId(Guid.Parse(descriptor.GeneratedByAssetId)));
            if (generatedView == null || generatedView.GeneratedByAssetId?.ToString() != descriptor.GeneratedByAssetId || generatedView.CanEditText
                || generatedView.VerifyGeneratedOutput == null || producer?.Document is not VisualScriptDocument
                || !generatedView.IsGeneratedOutputOwnedBy(producer))
                throw new InvalidOperationException("The generated source view lost its actual producer or read-only compiler output authority.");
            return;
        }
        var item = workspace!.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), descriptor.Path));
        if (entry.Document is AssetScriptSourceDocument source)
        {
            if (item == null || !ReferenceEquals(source.AuthoredAsset, item.Asset) || state.AssetId != item.Id)
                throw new InvalidOperationException("The retained recovered source asset is no longer the native package asset.");
        }
        else if (item != null || state.AssetId != null)
            throw new InvalidOperationException("The recovered project source acquired an unexpected package asset owner.");
    }
    private void RestoreExistingRecoverySource(EditorRecoveryRecord record)
    {
        var item = workspace!.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), record.Path));
        var descriptor = new EditorRecoveryOpenDocument(record.Path,
            item == null ? typeof(ScriptSourceDocument).FullName! : typeof(AssetScriptSourceDocument).FullName!,
            record.AssetId, record.PackagePath, item?.Package.Container?.AssetNamespace ?? item?.Package.AssetNamespace, record.ProjectPath);
        var proof = ValidateRecoverySourceMembership(descriptor);
        if (item?.Package.IsReadOnly == true) throw new InvalidOperationException("The recovered source package is read-only.");
        var entry = OpenRecoverySourcePresentation(descriptor);
        if (entry.Document is not ScriptSourceDocument source || source.GeneratedByAssetId != null
            || source.ProjectPath == null || !SamePath(source.ProjectPath, proof.ProjectPath))
            throw new InvalidOperationException("The recovered source generation has another native project owner.");
        ReplaceAuthoredDocument(entry, () => source.CreateRecoveryGeneration(record), "Recover " + entry.Name);
        ActivateDocument(entry);
    }
    private void RestoreNewRecoverySource(EditorRecoveryRecord record)
    {
        if (record.ProjectPath == null || record.AssetLocation == null || record.PackagePath == null || record.AssetId == null
            || !Path.GetExtension(record.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recovered source requires its actual C# project and source asset owner.");
        var project = workspace!.Projects.FirstOrDefault(project => SamePath(project.FullPath.ToOSPath(), record.ProjectPath))
            ?? throw new InvalidOperationException("The recovered source project is not loaded.");
        var package = project.Package;
        if (package.IsReadOnly || !SamePath(package.FullPath.ToOSPath(), record.PackagePath)) throw new InvalidOperationException("The recovered source has another package owner.");
        var root = Path.GetDirectoryName(project.FullPath.ToOSPath())!; var relative = Path.GetRelativePath(root, record.Path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            throw new InvalidOperationException("The recovered source path is outside the loaded authored project.");
        var projectState = authoring!.GetOrCreate(project.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(project, sessionHistory!));
        projectState.Document.ValidateProjectFileBaseline();
        var asset = new ScriptSourceFileAsset { Id = new AssetId(Guid.Parse(record.AssetId)) };
        var url = new UFile(record.AssetLocation); var item = AssetItem.CreateForPackage(package, package.Container?.Unqualify(url) ?? url, asset);
        item.SourceFolder = record.SourceFolder == null ? package.GetDefaultAssetFolder() : new UDirectory(record.SourceFolder);
        if (!SamePath(item.FullPath.ToOSPath(), record.Path)) throw new InvalidOperationException("The recovered source URL and folder do not own its file path.");
        var document = ScriptSourceDocument.CreateNewRecoverySource(record, asset, sessionHistory!);
        document.SetProjectOwnership(record.ProjectPath, folders: record.SourceFolders); document.BindSourceAssetItem(item);
        EditorAuthoringState state;
        try { state = authoring.RegisterUnpublished(document, item.Id); }
        catch { document.Dispose(); throw; }
        using var lease = state.AcquireView();
        var metadata = PackageMetadataRevision.Prepare(sessionHistory, workspace, [package]);
        var operation = new ScriptSourceTopologyOperation(this, package, item, state, projectState, true, metadata);
        try
        {
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                projectState.Document.ChangeProjectItem("Compile", item.GetProjectInclude(), true);
                using var transaction = sessionHistory.CreateTransaction("Recover new source");
                operation.ApplyInitial();
                try { sessionHistory.PushOperation(operation, operation.Participants); }
                catch (Exception failure)
                {
                    try { operation.CancelInitial(); }
                    catch (Exception recovery)
                    {
                        sessionHistory.RetainFailedInitialOperation(operation, operation.Participants);
                        throw new AggregateException("The recovered source publication could not be compensated.", failure, recovery);
                    }
                    throw;
                }
            });
            capture.Publish(sessionHistory, "Recover new source");
        }
        catch { if (!state.IsPublished) authoring.ReleaseIfUnused(state); throw; }
        workspace.RefreshAssets(); assetBrowser.RefreshEntries(workspace);
        var entry = documents.Add(document); ActivateDocument(entry);
    }
}
