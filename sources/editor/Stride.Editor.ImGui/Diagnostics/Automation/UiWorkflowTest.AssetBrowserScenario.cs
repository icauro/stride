using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Read actual semantic identities without causing AssetBrowserState cache rebuilds.
    internal AssetBrowserWorkflowState InspectAssetBrowserWorkflow(string? retainedAssetId = null)
    {
        var expected = workspace?.Assets.SingleOrDefault(asset => retainedAssetId != null ? asset.Id.ToString() == retainedAssetId
            : asset.Name == "Sphere" && asset.AssetType == typeof(ProceduralModelAsset));
        return new(expected?.Id.ToString(), expected?.Path, assetBrowser.Primary?.ToString(),
            expected != null && assetBrowser.IsSelected(expected.Id), selectedAssetPath, assetPreviewPath,
            assetBrowser.Names.Any(filter => filter.Enabled && filter.Text == "Sphere"),
            documents.Active?.Path, documents.Active?.State?.AssetId?.ToString(),
            modelPreview?.Name, modelPreview?.RenderCount, modelPreview?.RenderError?.ToString());
    }
}

internal sealed record AssetBrowserWorkflowState(string? ExpectedId, string? ExpectedPath, string? PrimaryId,
    bool ExpectedSelected, string? SelectedPath, string? PreviewPath, bool HasSphereNameFilter,
    string? ActiveDocumentPath, string? ActiveDocumentAssetId,
    string? CompiledPreviewUrl, int? PreviewRenderedFrames, string? PreviewRenderError);

internal sealed partial class UiWorkflowTest
{
    private SceneDocument? browserOriginalScene;
    private string? browserRetainedAssetId, browserInitialAssetPath, browserRenamedAssetPath;
    private int browserRelocationRound;
    private const string BrowserRenamedAsset = "SphereU04";

    private void UpdateAssetBrowserScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 10700:
                browserOriginalScene = game.Document;
                Assert(browserOriginalScene != null && !browserOriginalScene.IsDirty, "U04 requires a clean loaded fixture scene");
                Assert(game.InspectAssetBrowserWorkflow().ExpectedId != null, "U04 fixture must contain its actual Sphere procedural asset");
                if (Click("asset/filter")) Next();
                break;
            case 10701: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10702:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "Sphere") io.AddInputCharacter(character);
                Next(); break;
            case 10703: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10704: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10705:
                if (game.InspectAssetBrowserWorkflow().HasSphereNameFilter && Click("asset/Sphere")) Next();
                break;
            case 10706:
                var selected = game.InspectAssetBrowserWorkflow();
                if (selected.PrimaryId != selected.ExpectedId || !selected.ExpectedSelected
                    || !SameAssetPath(selected.SelectedPath, selected.ExpectedPath)
                    || !SameAssetPath(selected.PreviewPath, selected.ExpectedPath)) break;
                if (game.ModelPreview?.RenderError is { } previewError)
                    throw new InvalidOperationException("U04 compiled preview failed to render", previewError);
                if (game.ModelPreview is not { IsDisposed: false, RenderCount: > 0, MeshCount: > 0, MaterialPassCount: > 0 } preview) break;
                Assert(preview.IsProcedural && preview.SourceContent != null && preview.TargetWidth > 0
                    && preview.TargetHeight > 0 && preview.RenderError == null,
                    "Filtered selection must own real compiled procedural content and a rendered preview target");
                // Use the real dedicated-editor toolbar button; shortcut routing
                // and text-input eligibility need separate coverage.
                if (Click("asset/edit"))
                {
                    checks.Add("Committed name filter selects actual asset ID and presents its compiled engine preview");
                    Next();
                }
                break;
            case 10707:
                var opened = game.InspectAssetBrowserWorkflow();
                if (game.AssetDocument is not { } authored
                    || !SameAssetPath(opened.ActiveDocumentPath, opened.ExpectedPath)) break;
                Assert(authored.Asset.Id.ToString() == opened.ExpectedId && opened.ActiveDocumentAssetId == opened.ExpectedId
                    && SameAssetPath(authored.Path, opened.ExpectedPath) && !authored.IsDirty,
                    "Dedicated editor activation must bind the selected asset's actual canonical document and identity");
                Assert(ReferenceEquals(game.Document, browserOriginalScene) && !browserOriginalScene!.IsDirty,
                    "Browsing and opening an asset must preserve the original scene and its clean history state");
                var state = game.CaptureInspectionSnapshot();
                Assert(state.Documents.Any(entry => entry.AssetId == opened.ExpectedId && entry.Id == state.ActiveDocumentId),
                    "Inspection must identify the selected asset as the active retained document");
                checks.Add("Browser dedicated-editor button opens the canonical authored asset while retaining the original scene");
                browserRetainedAssetId = opened.ExpectedId;
                browserInitialAssetPath = opened.ExpectedPath;
                stage = 10720;
                break;
            case 10720:
                if (Click("asset/" + (browserRelocationRound == 0 ? "Sphere" : BrowserRenamedAsset))) Next(); break;
            case 10721: io.AddKeyEvent(ImGuiKey.F2, true); Next(); break;
            case 10722: io.AddKeyEvent(ImGuiKey.F2, false); Next(); break;
            case 10723:
                if (Click("asset/rename/" + browserRetainedAssetId)) Next(); break;
            case 10724: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 10725:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in browserRelocationRound == 0 ? BrowserRenamedAsset : "U04Moved/" + BrowserRenamedAsset)
                    io.AddInputCharacter(character);
                Next(); break;
            case 10726: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 10727: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 10728:
                var relocated = game.InspectAssetBrowserWorkflow(browserRetainedAssetId);
                var extension = Path.GetExtension(browserInitialAssetPath);
                var expectedLocation = browserRelocationRound == 0 ? BrowserRenamedAsset + extension
                    : Path.Combine("U04Moved", BrowserRenamedAsset + extension);
                if (relocated.ExpectedPath == null || !relocated.ExpectedPath.EndsWith(expectedLocation, StringComparison.OrdinalIgnoreCase)) break;
                Assert(File.Exists(relocated.ExpectedPath) && !File.Exists(browserRelocationRound == 0 ? browserInitialAssetPath : browserRenamedAssetPath),
                    "UI relocation must move actual asset bytes and remove the original file");
                Assert(game.AssetDocument?.Asset.Id.ToString() == browserRetainedAssetId
                    && SameAssetPath(game.AssetDocument.Path, relocated.ExpectedPath),
                    "Relocation must preserve canonical authored document identity and update its path");
                if (browserRelocationRound == 0) browserRenamedAssetPath = relocated.ExpectedPath;
                if (Click("toolbar/undo")) Next(); break;
            case 10729:
                var restored = game.InspectAssetBrowserWorkflow(browserRetainedAssetId);
                Assert(SameAssetPath(restored.ExpectedPath, browserRelocationRound == 0 ? browserInitialAssetPath : browserRenamedAssetPath)
                    && File.Exists(restored.ExpectedPath), "Undo relocation must restore the original authored path and bytes");
                if (Click("toolbar/redo")) Next(); break;
            case 10730:
                var replayed = game.InspectAssetBrowserWorkflow(browserRetainedAssetId);
                Assert(replayed.ExpectedPath != null && File.Exists(replayed.ExpectedPath)
                    && !SameAssetPath(replayed.ExpectedPath, browserRelocationRound == 0 ? browserInitialAssetPath : browserRenamedAssetPath),
                    "Redo relocation must reapply actual file movement with stable asset identity");
                if (browserRelocationRound == 0)
                { browserRelocationRound = 1; checks.Add("Inline F2 rename preserves identity, file data and undo/redo"); stage = 10720; }
                else if (Click("toolbar/save")) Next();
                break;
            case 10731:
                var persisted = game.InspectAssetBrowserWorkflow(browserRetainedAssetId);
                var snapshot = game.CaptureInspectionSnapshot();
                if (snapshot.Documents.Any(entry => entry.IsDirty) || snapshot.Project.HasPackageChanges) break;
                var asset = Stride.Core.Assets.AssetFileSerializer.Load<ProceduralModelAsset>(persisted.ExpectedPath!).Asset;
                var scene = Stride.Core.Assets.AssetFileSerializer.Load<Stride.Assets.Entities.SceneAsset>(browserOriginalScene!.Path).Asset;
                var model = scene.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity.Get<Stride.Engine.ModelComponent>().Model;
                var reference = Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(model);
                Assert(asset.Id.ToString() == browserRetainedAssetId && reference?.Id.ToString() == browserRetainedAssetId
                    && reference.Url.EndsWith("U04Moved/" + BrowserRenamedAsset, StringComparison.Ordinal),
                    "Independent moved asset and dependent scene reload must retain identity and destination reference URL");
                checks.Add("Destination path input moves real asset; Save All persists its dependent scene reference");
                CompleteIndependentSubset(); break;
        }
    }

    private static bool SameAssetPath(string? left, string? right) => left != null && right != null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
