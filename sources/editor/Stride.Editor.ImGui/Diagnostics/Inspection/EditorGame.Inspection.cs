namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    /// <summary>
    /// Capture on the editor actor (e.g. UiWorkflowTest.Update). Does not commit edits,
    /// pump jobs, acquire leases, touch ImGui context, or read back GPU pixels.
    /// Never invoke from a background polling thread.
    /// </summary>
    internal EditorInspectionSnapshot CaptureInspectionSnapshot(bool includeHierarchy = false)
    {
        var hierarchy = InspectPresentedHierarchy();
        var load = Volatile.Read(ref projectLoadState);
        var viewport = InspectionViewport;
        return new(1, frames, statusMessage, documents.Active?.Id,
            Array.AsReadOnly(documents.Entries.Select(entry => new DocumentInspectionSnapshot(
                entry.Id, entry.State?.Generation, entry.State?.AssetId?.ToString(), entry.Path, entry.Kind,
                entry.Document.IsDirty, entry.Document.CanSave, entry.Document.CanUndo, entry.Document.CanRedo,
                entry.State?.ViewReferences, entry.State?.HistoryReferences)).ToArray()),
            new(selectedAssetPath, hierarchy?.Asset.Id.ToString(), hierarchy?.Selected?.Id,
                Array.AsReadOnly(hierarchy?.SelectedIds.ToArray() ?? []), hierarchy?.SelectionVersion),
            new(workspace?.OpenPath, workspace?.PackagePath, workspace?.HasActivePackage == true,
                workspace?.HasPackageChanges == true, workspace?.HasCompiledContent == true,
                workspace?.Assets.Count ?? 0, workspace?.CompilationFailure?.ToString(), load?.Phase, load?.Detail),
            Array.AsReadOnly(new[] {
                InspectOperation("project.load", projectLoading, projectCancellation),
                InspectOperation("asset.refresh", assetPreviewBuild, assetPreviewCancellation),
                InspectOperation("asset.import", importSettingsBuild, importSettingsCancellation),
                InspectOperation("preview.media", mediaPreviewBuild, mediaPreviewCancellation),
                InspectOperation("preview.texture", textureSettingsBuild, textureSettingsCancellation),
            }),
            new(viewport != null, viewport?.Visible == true, viewport?.RuntimeScene != null,
                viewport?.UsesAuthoredCompositor == true, viewport?.CompiledSceneUrl,
                viewport?.Target?.Width ?? 0, viewport?.Target?.Height ?? 0,
                viewport?.ModelCount ?? 0, viewport?.BackgroundCount ?? 0, engineDrawCalls,
                viewport?.IsSceneRetirementStarted == true, viewport?.IsSceneRetirementDrainComplete ?? true,
                Array.AsReadOnly(viewport?.Gaps.ToArray() ?? [])),
            Array.AsReadOnly(new[] {
                new PanelInspectionSnapshot("scene.hierarchy", showHierarchy),
                new PanelInspectionSnapshot("editor.properties", showInspector),
                new PanelInspectionSnapshot("assets.browser", showAssets),
                new PanelInspectionSnapshot("editor.output", showOutput),
            }), retiredEditorPreviews.Count,
            includeHierarchy && hierarchy != null ? Array.AsReadOnly(hierarchy.Asset.Hierarchy.Parts.Values
                .Select(part => part.Entity).Select(entity => new EntityInspectionSnapshot(
                    entity.Id, entity.Name, entity.Transform.Parent?.Entity.Id,
                    Array.AsReadOnly(entity.Transform.Children.Select(child => child.Entity.Id).ToArray()),
                    Array.AsReadOnly(entity.Components.Select(component => new ComponentInspectionSnapshot(
                        component.Id, component.GetType().FullName ?? component.GetType().Name)).ToArray()),
                    new(entity.Transform.Position.X, entity.Transform.Position.Y, entity.Transform.Position.Z),
                    new(entity.Transform.Rotation.X, entity.Transform.Rotation.Y, entity.Transform.Rotation.Z, entity.Transform.Rotation.W),
                    new(entity.Transform.Scale.X, entity.Transform.Scale.Y, entity.Transform.Scale.Z),
                    viewport != null && ReferenceEquals(hierarchy, viewport.PresentedDocument)
                        ? viewport.MatchesTransform(entity) : null))
                .ToArray()) : null);
    }

    private static OperationInspectionSnapshot InspectOperation(string id, Task? task, CancellationTokenSource? cancellation)
        => new(id, task != null, task?.Status.ToString(), cancellation?.IsCancellationRequested);

    // The compatibility engineViewport is temporarily rebound while individual
    // views draw, then restored. Resolve the published document's retained owner
    // instead of inspecting that frame-local binding between draw phases.
    internal EngineViewport? InspectionViewport
    {
        get
        {
            var entry = documents.Active?.Document is EntityHierarchyDocument ? documents.Active : documents.Find(document);
            if (entry != null && sceneDocumentViews.TryGetValue(entry.Id, out var view))
                return IsCurrentSceneDocumentView(view) ? view.Viewport : null;
            return entry != null && IsCurrentDocumentPresentation(entry) && document != null
                && ReferenceEquals(engineViewport?.PresentedDocument, document)
                ? engineViewport : null;
        }
    }

    // The normal presentation getter reuses a mutable scratch set. Inspection
    // resolves the same actual ownership with a private set, leaving host state untouched.
    private EntityHierarchyDocument? InspectPresentedHierarchy()
    {
        if (InspectionViewport?.PresentedDocument is { } runtimeRoot) return runtimeRoot;
        if (cpuPresentedHierarchyId is { } id && authoring?.Find(id) is { IsPublished: true, Document: SceneDocument root }
            && documents.Find(root) != null && document is SceneDocument current)
        {
            var visited = new HashSet<Stride.Core.Assets.AssetId>();
            while (visited.Add(current.Asset.Id))
            {
                if (ReferenceEquals(current, root)) return root;
                if (SceneParentId(current.Asset) is not { } parent
                    || authoring.Find(parent) is not { IsPublished: true, Document: SceneDocument next }
                    || !next.Asset.ChildrenIds.Contains(current.Asset.Id)) break;
                current = next;
            }
        }
        return document;
    }
}
