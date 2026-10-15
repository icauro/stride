using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RelocateVisualScriptAsset(AssetEntry entry, string url, Package? destinationPackage = null)
    {
        var item = workspace!.FindAsset(entry.Id);
        if (!CanRelocateAsset(entry) || item.Package.Container is not SolutionProject project) throw new InvalidOperationException("The visual script cannot be moved.");
        var targetPackage = destinationPackage ?? item.Package;
        if (!IsEditableAssetPackage(targetPackage) || !ReferenceEquals(targetPackage.Session, item.Package.Session) || targetPackage.Container is not SolutionProject targetProject)
            throw new InvalidOperationException("The visual script requires an editable target project.");
        var location = targetProject.Qualify(new UFile(url.Replace('\\', '/')));
        if (ReferenceEquals(targetPackage, item.Package) && string.Equals(location.FullPath, item.Location.FullPath, StringComparison.Ordinal)) return;
        AssetRelocationHistory.ValidateUrl(url.Replace('\\', '/'));
        if (targetPackage.Assets.Any(candidate => candidate.Id != item.Id && string.Equals(candidate.UnqualifiedUrl.FullPath, url.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(item.Package, targetPackage) && candidate.Id == item.Id))
            throw new InvalidOperationException("An asset with the same name exists in the same directory.");
        ValidatePendingPropertyStrings();
        var visual = (VisualScriptDocument)GetAssetTagDocument(entry, false); visual.CommitEdits();
        var producer = authoring!.Find(item.Id) ?? throw new InvalidOperationException("The visual script has no canonical producer.");
        using var producerLease = producer.AcquireView();
        var projectDocument = GetProjectBuildSettingsDocument(project, sessionHistory!); projectDocument.ValidateProjectFileBaseline();
        var targetProjectDocument = GetProjectBuildSettingsDocument(targetProject, sessionHistory!); targetProjectDocument.ValidateProjectFileBaseline();
        var sourceItemType = projectDocument.ProjectSourceItemType(item);
        var beforeInclude = item.GetProjectInclude(); var beforeGeneratedInclude = item.GetGeneratedInclude();
        var generatedPath = item.GetGeneratedAbsolutePath().ToOSPath();
        var output = authoring.Find(generatedPath);
        if (output == null && File.Exists(generatedPath))
        {
            var generated = (ScriptSourceDocument)GetCanonicalDocument(generatedPath, () => new ScriptSourceDocument(generatedPath, true, sessionHistory));
            generated.BindGeneratedOutput(item.Id, project.FullPath.ToOSPath(), false, producer); output = authoring.Find(generatedPath);
        }
        if (output != null && (output.Document is not ScriptSourceDocument source || source.GeneratedByAssetId != item.Id))
            throw new InvalidOperationException("The generated path belongs to another authored source.");
        using var outputLease = output?.AcquireView();
        var retained = documents.AuthoredDocuments.OfType<IAssetReferenceDocument>().ToDictionary(document => document.AuthoredAsset.Id);
        var moved = false;
        void Apply()
        {
            moved = assetRelocationHistory.Relocate(workspace, entry, url, retained,
                asset => (IAssetReferenceDocument)GetAssetTagDocument(asset, false),
                (from, to) => RelocateVisualScriptDocumentPaths(visual, producer,
                    string.Equals(Path.GetFullPath(to), Path.GetFullPath(entry.Path), StringComparison.Ordinal) ? project : targetProject, from, to), targetPackage);
            if (!moved) return;
            var next = workspace.Session.FindAsset(item.Id) ?? throw new InvalidOperationException("The moved visual-script identity disappeared.");
            targetProjectDocument.TransferProjectItemFrom(projectDocument, sourceItemType, beforeInclude, next.GetProjectInclude());
            targetProjectDocument.TransferProjectItemFrom(projectDocument, "Compile", beforeGeneratedInclude, next.GetGeneratedInclude());
            targetProjectDocument.ChangeGeneratedAssetMembership(next, true);
            InitializeVisualScriptDocument(visual); UpdateVisualScriptGeneratedOutput(visual);
        }
        if (sessionHistory!.IsCapturingOperations) Apply();
        else { using var capture = sessionHistory.CaptureOperationsAtomic(Apply); if (moved) capture.Publish(sessionHistory, "Rename visual script"); }
        if (!moved) return;
        assetRelocationCommandContext = true; assetDeletionCommandContext = assetTagBatchCommandContext = packageFolderCommandContext = false;
        RefreshRelocatedAssets(entry.Id);
    }
    private void RelocateVisualScriptDocumentPaths(VisualScriptDocument visual, EditorAuthoringState producer,
        SolutionProject project, string from, string to)
    {
        var generatedFrom = from + ".cs"; var generatedTo = to + ".cs";
        var output = authoring!.Find(generatedFrom);
        if (output?.Document is not null && (output.Document is not ScriptSourceDocument source || source.GeneratedByAssetId != visual.Asset.Id))
            throw new InvalidOperationException("The generated source belongs to another producer.");
        if (output != null && !string.Equals(generatedFrom, generatedTo, StringComparison.OrdinalIgnoreCase)
            && (authoring.Find(generatedTo) != null || File.Exists(generatedTo) || Directory.Exists(generatedTo)))
            throw new IOException("The destination generated source path is occupied.");
        var moved = false; var generatedMoved = false;
        try
        {
            RelocateAssetDocumentPath(from, to); moved = true;
            if (output?.Document is ScriptSourceDocument generated)
            {
                RelocateAssetDocumentPath(generatedFrom, generatedTo); generatedMoved = true;
                generated.BindGeneratedOutput(visual.Asset.Id, project.FullPath.ToOSPath(), false, producer);
                assetRelocationHistory.TrackSourceMove(generatedFrom, generatedTo);
            }
        }
        catch
        {
            if (generatedMoved) RelocateAssetDocumentPath(generatedTo, generatedFrom);
            if (moved) RelocateAssetDocumentPath(to, from);
            throw;
        }
    }
}
