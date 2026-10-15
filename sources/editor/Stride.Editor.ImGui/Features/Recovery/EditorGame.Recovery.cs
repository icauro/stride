using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorRecoveryPanel? ownedRecovery;
    private void InitializeRecovery()
    {
        pendingRecoveryWorkspaceRestore = null;
        recoveryWorkspaceNativeFocusPending = false;
        ownedRecovery = workspace == null ? null : new(new EditorRecoveryHost(this), RecoveryVerificationRoot(),
            CaptureRecoveryNativeButtonsEnabled());
    }
    private bool CaptureRecoveryNativeButtonsEnabled()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--workspace-recovery-fixture")) return false;
        return workspace != null && SamePath(WorkspaceRecoveryFixtureGuard.Resolve(workspace.OpenPath, args), workspace.OpenPath);
    }
    private static string? RecoveryVerificationRoot()
    {
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--recovery-test-root");
        if (index < 0) return null;
        if (!arguments.Contains("--ui-test") || index + 1 >= arguments.Length)
            throw new InvalidOperationException("A private recovery journal requires the disposable UI test host.");
        return Path.GetFullPath(arguments[index + 1]);
    }
    internal Task BeginRecoveryWorkspaceVerificationCheckpoint()
        => ownedRecovery?.FlushTransitionAsync() ?? throw new InvalidOperationException("The recovery host is not loaded.");
    internal EditorRecoveryWorkspaceState CaptureRecoveryWorkspaceVerification() => CaptureRecoveryWorkspace();
    internal RecoveryWorkspaceButtonReceipt? InspectRecoveryWorkspaceButton(Guid session) => ownedRecovery?.InspectWorkspaceButton(session);
    internal bool IsRecoveryWorkspaceRestorePending => pendingRecoveryWorkspaceRestore != null || pendingRecoveryWorkspaceCompensation != null;
    private void TickRecovery() { if (!IsRecoveryWorkspaceRestorePending) ownedRecovery?.Tick(); }
    private void DrawRecoveryDialog() => ownedRecovery?.Draw();
    private void RequestRecoveryDialog() => ownedRecovery?.RequestDialog();
    private bool CanRequestRecoveryDialog => ownedRecovery?.HasRecoverableCandidates == true
        && workspace != null && projectLoading == null && !RecoveryTransitionBusy && !IsRecoveryWorkspaceRestorePending
        && !IsAuthoringPipelineUpdating && !IsEditorGraphicsRecovering;
    private void CheckpointRecovery() => ownedRecovery?.Checkpoint();
    private Task CompleteRecoverySessionAsync(bool discardPendingChanges)
        => ownedRecovery?.CompleteAsync(discardPendingChanges) ?? Task.CompletedTask;
    private void ValidateRecoveredPackageBaselines()
    {
        foreach (var owner in documents.AuthoredDocuments.OfType<PackageMetadataDocument>()) owner.ValidateRecoveryDiskBaseline();
    }
    private sealed class EditorRecoveryHost(EditorGame editor) : IEditorRecoveryHost
    {
        public string ProjectPath => editor.workspace!.OpenPath;
        public bool HasPendingChanges => editor.workspace!.HasPackageChanges || editor.documents.AuthoredDocuments.Any(document => document.IsDirty);
        public EditorRecoveryWorkspaceState CaptureWorkspace() => editor.CaptureRecoveryWorkspace();
        public void RestoreWorkspace(EditorRecoveryWorkspaceState state) => editor.QueueRecoveryWorkspaceRestore(state);
        public string? WorkspaceDependencyFailure(EditorRecoveryWorkspaceState state) => editor.GetRecoveryWorkspaceDependencyFailure(state);
        public void RecordControl(string id) => editor.Controls.Record(id);
        public IReadOnlyList<EditorRecoveryRecord> CaptureDocuments()
        {
            var workspace = editor.workspace!;
            foreach (var package in workspace.Session.LocalPackages.Where(package => package.IsDirty))
                editor.authoring!.GetOrCreate(package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(workspace, package, editor.sessionHistory!));
            var result = new List<EditorRecoveryRecord>();
            foreach (var document in editor.documents.AuthoredDocuments.Where(document => document.IsDirty).ToArray())
            {
                if (document is ScriptSourceDocument { GeneratedByAssetId: not null }) continue;
                var item = workspace.AssetItems.FirstOrDefault(item => string.Equals(item.FullPath.ToOSPath(), document.Path, StringComparison.OrdinalIgnoreCase));
                try { result.Add(EditorRecoveryDocuments.Capture(document, item)); }
                catch (Exception error)
                {
                    throw new InvalidOperationException($"Recovery capture failed for {document.GetType().FullName} at '{document.Path}' (native asset owner: {item?.Id.ToString() ?? "none"}).", error);
                }
            }
            return result;
        }
        public string[] LoadedAssemblies() => editor.workspace!.Session.Packages.SelectMany(package => package.LoadedAssemblies)
            .Select(owner => owner.Assembly).Distinct().Select(assembly => assembly.FullName + "|" + assembly.ManifestModule.ModuleVersionId)
            .Order(StringComparer.Ordinal).ToArray();
        public void Restore(EditorRecoveryRecord record)
        {
            var workspace = editor.workspace!;
            if (record.Kind == "package-settings")
            {
                var package = workspace.Session.LocalPackages.SingleOrDefault(package => record.PackagePath != null
                    && SamePath(package.FullPath.ToOSPath(), record.PackagePath))
                    ?? throw new InvalidOperationException("The recovery package user-settings owner is not loaded.");
                GetPackageSettingsDocument(package, editor.sessionHistory!).RestoreRecovery(record); return;
            }
            if (record.Kind == "package")
            {
                var package = workspace.Session.LocalPackages.FirstOrDefault(package => SamePath(package.FullPath.ToOSPath(), record.Path))
                    ?? throw new InvalidOperationException("The recovery package is not loaded.");
                var owner = editor.authoring!.GetOrCreate(record.Path, null, () => new PackageMetadataDocument(workspace, package, editor.sessionHistory!));
                owner.Document.RestoreRecovery(record); return;
            }
            if (record.Kind == "project")
            {
                var project = workspace.Projects.FirstOrDefault(project => SamePath(project.FullPath.ToOSPath(), record.Path))
                    ?? throw new InvalidOperationException("The recovery project is not loaded.");
                GetProjectBuildSettingsDocument(project, editor.sessionHistory!).RestoreRecovery(record); return;
            }
            var canonical = workspace.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), record.Path));
            if (record.Kind == "source" && File.Exists(record.Path) && record.BaselineHash.Length != 0)
            {
                editor.RestoreExistingRecoverySource(record); return;
            }
            if (canonical == null)
            {
                RestoreNewAsset(record); return;
            }
            if (canonical.Id.ToString() != record.AssetId || canonical.Package.IsReadOnly || record.PackagePath == null
                || !SamePath(canonical.Package.FullPath.ToOSPath(), record.PackagePath)) throw new InvalidOperationException("The recovery path belongs to another package asset.");
            var target = editor.documents.Find(record.Path);
            if (target == null)
            {
                if (record.Kind == "source")
                {
                    var source = editor.GetCanonicalDocument(record.Path, () => editor.CreateScriptDocument(record.Path));
                    target = editor.documents.Add(source);
                }
                else
                {
                    var entry = new AssetEntry(canonical.FullPath.ToOSPath(), canonical.UnqualifiedUrl.ToString(), System.IO.Path.GetExtension(canonical.FullPath.ToOSPath()), canonical.Asset.GetType(), canonical.Id, canonical.Asset.Tags.ToArray());
                    var document = editor.GetAssetTagDocument(entry, false); target = editor.documents.Find((IEditorDocument)document)!;
                }
            }
            var current = target.Document; var log = new LoggerResult();
            editor.ReplaceAuthoredDocument(target, () => EditorRecoveryDocuments.RestoreGeneration(current, record, log), "Recover " + target.Name);
            editor.ActivateDocument(target);
        }
        private void RestoreNewAsset(EditorRecoveryRecord record)
        {
            if (record.BaselineHash.Length != 0 || File.Exists(record.Path) || Directory.Exists(record.Path)
                || editor.authoring!.Find(record.Path) != null) throw new InvalidOperationException("The missing recovery asset has an occupied or changed original disk path.");
            if (record.Kind == "source") { editor.RestoreNewRecoverySource(record); return; }
            var package = editor.workspace!.Session.LocalPackages.FirstOrDefault(package => record.PackagePath != null && SamePath(package.FullPath.ToOSPath(), record.PackagePath))
                ?? throw new InvalidOperationException("The recovery asset package is not loaded.");
            if (package.IsReadOnly || string.IsNullOrWhiteSpace(record.AssetLocation)) throw new InvalidOperationException("The recovery package cannot accept this asset.");
            var loaded = EditorRecoveryDocuments.LoadRecoveryAsset(record, new LoggerResult());
            var url = new UFile(record.AssetLocation); var item = AssetItem.CreateForPackage(package, package.Container?.Unqualify(url) ?? url, loaded.Asset);
            item.SourceFolder = record.SourceFolder == null ? package.GetDefaultAssetFolder() : new UDirectory(record.SourceFolder);
            loaded.YamlMetadata.CopyInto(item.YamlMetadata);
            if (!SamePath(item.FullPath.ToOSPath(), record.Path)) throw new InvalidOperationException("The recovered asset URL and package folder do not own the recorded file path.");
            editor.CreatePreparedAsset(package, item, record.Path, "Recover new asset");
            var entry = new AssetEntry(record.Path, item.UnqualifiedUrl.ToString(), System.IO.Path.GetExtension(record.Path), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
            editor.GetAssetTagDocument(entry);
        }
        public void ReportFailure(Exception error) { editor.statusMessage = error.Message; editor.RecordAssetPreviewRenderFailure("recovery", error); }
        public void DrawSelectableText(string id, string value) => editor.DrawSelectableText(id, value);
    }
}
