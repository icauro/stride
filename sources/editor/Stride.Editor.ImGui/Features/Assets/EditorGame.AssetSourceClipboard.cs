using System.Text;
using Stride.Assets.Scripts;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanCutSourceAsset(AssetEntry entry)
    {
        var item = workspace!.FindAsset(entry.Id);
        return item.Asset is SourceCodeAsset && (item.Asset is not IProjectAsset || item.Package.Container is SolutionProject)
            && IsEditableAssetPackage(item.Package) && item.AlternativePath == null;
    }
    private void CutSourceAssets(AssetEntry[]? selection = null, bool writeClipboard = true)
    {
        var targets = selection ?? assetBrowser.SelectedEntries.ToArray();
        if (targets.Length == 0 || !targets.All(CanCutSourceAsset)) return;
        documents.CommitAll();
        var history = sessionHistory!;
        var prepared = new List<(AssetItem Item, ProjectBuildSettingsDocument? Project, ScriptSourceTopologyOperation Operation)>();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var entry in targets)
            {
                var item = workspace!.FindAsset(entry.Id);
                var project = item.Asset is IProjectAsset ? (SolutionProject)item.Package.Container : null;
                var projectDocument = project == null ? null : GetProjectBuildSettingsDocument(project, history);
                projectDocument?.ValidateProjectFileBaseline();
                var source = GetCanonicalDocument(entry.Path, () => CreateScriptDocument(entry.Path)) as ScriptSourceDocument
                    ?? throw new InvalidOperationException("The source has another canonical owner.");
                if (!source.CanEditText) throw new InvalidOperationException("The source can't be cut.");
                source.CommitEdits();
                var state = authoring!.Find(item.Id) ?? throw new InvalidOperationException("The source has no canonical project owner.");
                leases.Add(state.AcquireView());
                var projectState = project == null ? null : authoring.Find(project.FullPath.ToOSPath());
                var metadata = PackageMetadataRevision.Prepare(history, workspace, [item.Package]);
                prepared.Add((item, projectDocument, new ScriptSourceTopologyOperation(this, item.Package, item, state, projectState, false, metadata)));
            }
            if (writeClipboard)
            {
                var text = SerializeAssetClipboard(prepared.Select(entry => entry.Item).ToArray());
                if (text.Length == 0) return;
                Hexa.NET.ImGui.ImGui.SetClipboardText(text);
                if (Hexa.NET.ImGui.ImGui.GetClipboardTextS() != text) throw new InvalidOperationException("The sources could not be written to the clipboard.");
            }
            void Apply()
            {
                foreach (var entry in prepared)
                {
                    entry.Project?.ChangeProjectSourceMembership(entry.Item, false);
                    entry.Operation.ApplyInitial();
                    try { history.PushOperation(entry.Operation, entry.Operation.Participants); }
                    catch (Exception failure)
                    {
                        try { entry.Operation.CancelInitial(); }
                        catch (Exception recovery)
                        {
                            history.RetainFailedInitialOperation(entry.Operation, entry.Operation.Participants);
                            throw new AggregateException("The source cut could not be recorded or restored.", failure, recovery);
                        }
                        throw;
                    }
                }
            }
            if (history.IsCapturingOperations) Apply();
            else { using var capture = history.CaptureOperationsAtomic(Apply); capture.Publish(history, "Cut sources"); }
            assetBrowser.RefreshEntries(workspace!); assetBrowser.RestoreSelection([], null); ActivateAssetPropertyContext();
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
