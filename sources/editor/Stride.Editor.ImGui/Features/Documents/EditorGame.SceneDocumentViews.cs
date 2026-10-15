using Hexa.NET.ImGui;
using Stride.Games;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SceneDocumentView : IDisposable, IEditorResourceRetirementDrain
    {
        internal required EditorDocumentEntry Entry;
        internal required EntityHierarchyDocument Document;
        internal required EditorAuthoringState State;
        internal required ProjectWorkspace Workspace;
        internal required CompiledProjectContent ProjectContent;
        internal required CompiledProjectContent Content;
        internal required EngineViewport Viewport;
        internal readonly Dictionary<ImportContentOwner, ImportedContentLease> Imports = [];
        internal Func<SceneDocument, bool>? LoadedPolicy;
        internal SceneOwnerRebindSnapshot? OwnerRebind;
        private bool disposedViewport;
        public bool IsRetirementDrainComplete => Viewport.IsSceneRetirementDrainComplete;
        public bool PumpRetirementDrain() => Viewport.PumpSceneRetirementDrain();
        public void Dispose()
        {
            if (!disposedViewport) { Viewport.Dispose(); disposedViewport = true; }
            OwnerRebind?.ContentLease.Dispose(); OwnerRebind = null;
            // Native SceneViewportResources owns the transferred leases through
            // its actual worker/actor/GPU/cache release phases.
            Imports.Clear();
        }
    }
    private sealed record SceneOwnerRebindSnapshot(IDisposable ContentLease, EngineViewportCompiledInputs Inputs,
        Action<SceneDocumentView> RestoreControls, bool Visible);
    private readonly Dictionary<Guid, SceneDocumentView> sceneDocumentViews = [];
    private readonly Dictionary<Guid, EditorDocumentEntry> visibleSceneDocuments = [];
    private readonly HashSet<SceneDocumentView> drainingSceneDocumentViews = [];
    private readonly HashSet<SceneDocumentView> retiredSceneDocumentBorrowers = [];
    private readonly Dictionary<Guid, (IEditorDocument Owner, EditorAuthoringState? State, CompiledProjectContent Content, SceneSynchronizationState Revision)> failedSceneViewPreparation = [];

    private bool IsCurrentOrRetainedSceneProjectContent(CompiledProjectContent content)
        => ReferenceEquals(projectContent, content) || retainedActiveProjectContent.Contains(content);

    private bool IsCurrentSceneDocumentView(SceneDocumentView view)
        => IsCurrentDocumentPresentation(view.Entry) && ReferenceEquals(view.Entry.Document, view.Document)
            && ReferenceEquals(view.Entry.State, view.State) && ReferenceEquals(workspace, view.Workspace)
            && IsCurrentOrRetainedSceneProjectContent(view.ProjectContent) && ReferenceEquals(view.Viewport.SourceContent, view.Content)
            && ReferenceEquals(view.Viewport.PresentedDocument, view.Document) && view.Viewport.RuntimeScene != null;
    private bool CanPresentSceneDocument(EditorDocumentEntry entry)
        => !IsEditorGpuRetirementPending && !IsEditorGraphicsRecovering && projectContent != null
            && sceneDocumentViews.TryGetValue(entry.Id, out var view) && IsCurrentSceneDocumentView(view);

    private bool RebindPresentedSceneDocumentViews(EntityHierarchyDocument previous, EntityHierarchyDocument next)
    {
        var affected = sceneDocumentViews.Values.Where(view => ReferenceEquals(view.Document, previous)
            || ReferenceEquals(view.Entry.Document, next)
                && (view.OwnerRebind != null || !ReferenceEquals(view.Document, next)
                    || !ReferenceEquals(view.State, view.Entry.State)
                    || !ReferenceEquals(view.Viewport.PresentedDocument, next))).ToArray();
        if (affected.Length == 0) return false;
        foreach (var view in affected)
        {
            if (!IsCurrentDocumentPresentation(view.Entry) || !ReferenceEquals(view.Entry.Document, next)
                || view.Entry.State == null || !ReferenceEquals(view.Workspace, workspace)
                || !IsCurrentOrRetainedSceneProjectContent(view.ProjectContent))
                throw new InvalidOperationException("The replacement scene view has no current canonical owner.");
            if (view.OwnerRebind == null)
            {
                if (view.Viewport.CompiledInputs is not { } inputs || !ReferenceEquals(view.Viewport.SourceContent, view.Content))
                    throw new InvalidOperationException("The replacement scene view has no retained compiled inputs.");
                var restore = CaptureSceneDocumentRecoveryControls(view);
                view.OwnerRebind = new(view.Viewport.AcquireSceneContentRecoveryLease(), inputs, restore, view.Viewport.Visible);
            }
            var snapshot = view.OwnerRebind;
            // Load transfers previous graph/content/import leases into the native
            // phased retirement owner. The retained snapshot also survives a
            // failed load until the outer canonical swap republishes its owner.
            view.Viewport.Load(next, view.Workspace, view.Content, snapshot.ContentLease, snapshot.Inputs);
            view.Imports.Clear();
            view.Document = next; view.State = view.Entry.State;
            ReplaySceneDocumentResources(view);
            snapshot.RestoreControls(view); view.Viewport.Visible = snapshot.Visible;
            if (!IsCurrentSceneDocumentView(view))
                throw new InvalidOperationException("The replacement scene view owner changed before publication.");
            ForgetSceneViewSynchronization(view.Viewport); ForgetSceneCompositorView(view.Viewport);
            failedSceneViewPreparation.Remove(view.Entry.Id);
            snapshot.ContentLease.Dispose(); view.OwnerRebind = null;
        }
        return true;
    }

    private SceneDocumentView EnsureSceneDocumentView(EditorDocumentEntry entry)
    {
        if (!IsCurrentDocumentPresentation(entry) || entry.Document is not EntityHierarchyDocument scene
            || projectContent == null || workspace == null || renderer == null)
            throw new InvalidOperationException("The actual scene document and compiled content must be ready.");
        if (sceneDocumentViews.TryGetValue(entry.Id, out var current))
        {
            if (IsCurrentSceneDocumentView(current)) return current;
            ReleaseSceneDocumentView(entry);
        }
        var viewport = ReferenceEquals(engineViewport?.PresentedDocument, scene) && ReferenceEquals(engineViewport.SourceContent, projectContent)
            ? engineViewport : new EngineViewport(this, renderer)
            {
                ResolveChildSceneDocument = ResolveSceneHierarchyDocument,
                ResolveSceneWorldOffset = GetSceneHierarchyOffset,
                SceneLoaded = IsSceneLoaded, SceneLocked = IsSceneLocked, EntityLocked = IsHierarchyEntityLocked,
                ScenePresentationVersion = () => scenePresentationVersion,
            };
        var next = new SceneDocumentView { Entry = entry, Document = scene, State = entry.State!, Workspace = workspace,
            ProjectContent = projectContent, Content = projectContent, Viewport = viewport };
        next.LoadedPolicy = candidate => IsSceneDocumentViewLoaded(viewport, candidate);
        viewport.SceneLoaded = next.LoadedPolicy;
        try
        {
            if (!ReferenceEquals(viewport.PresentedDocument, scene)) viewport.Load(scene, workspace, projectContent);
            ReplaySceneDocumentResources(next);
            if (!IsCurrentSceneDocumentView(next)) throw new InvalidOperationException("The scene view owner changed before publication.");
            sceneDocumentViews.Add(entry.Id, next);
            return next;
        }
        catch
        {
            viewport.Visible = false;
            if (ReferenceEquals(engineViewport, viewport)) engineViewport = null;
            drainingSceneDocumentViews.Add(next); retiredSceneDocumentBorrowers.Add(next); RetireEditorPreview(next);
            viewport.BeginSceneRetirementDrain();
            throw;
        }
    }

    private void ReplaySceneDocumentResources(SceneDocumentView view)
    {
        foreach (var resource in refreshedModels) view.Viewport.ReplaceRuntimeModel(resource.Url, resource.Model);
        foreach (var resource in refreshedSceneTextures) view.Viewport.ReplaceRuntimeTexture(resource.Key, resource.Value.Texture, view.Document);
        ReplayCompiledSpriteSheets(view.Viewport, view.Document);
        foreach (var installed in installedSkyboxes.Values)
            if (TryGetRefreshedSkybox(installed.Url, out var skybox)) view.Viewport.ReplaceRuntimeSkybox(installed.Url, skybox, view.Document);
        foreach (var owner in importedContentOwners.Values.OrderBy(owner => owner.Generation)) ReplaySceneDocumentImports(view, owner);
    }
    private void ReplaySceneDocumentImports(SceneDocumentView view, ImportContentOwner owner)
    {
        RetainSceneDocumentImportOwner(view.Viewport, owner, view);
        view.Viewport.ReplaceRuntimeImportedContent(owner.Values, view.Document);
    }
    private void RetainSceneDocumentImportOwner(EngineViewport viewport, ImportContentOwner owner, SceneDocumentView? prepared = null)
    {
        var view = prepared ?? sceneDocumentViews.Values.FirstOrDefault(view => ReferenceEquals(view.Viewport, viewport));
        if (view != null && view.Imports.ContainsKey(owner)) return;
        var lease = new ImportedContentLease(owner);
        try { viewport.RetainSceneLease(lease); }
        catch { lease.Dispose(); throw; }
        view?.Imports.Add(owner, lease);
    }

    private void PrepareSceneDocumentViews()
    {
        if (workspace == null || projectContent == null || renderer == null || IsAuthoringPipelineUpdating
            || IsAssetPreviewUpdating || projectLoading != null || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering) return;
        foreach (var entry in documents.Entries)
        {
            if (entry.Document is not EntityHierarchyDocument scene || !IsCurrentDocumentPresentation(entry)) continue;
            var certificate = (entry.Document, entry.State, projectContent, scene.SynchronizationState);
            if (failedSceneViewPreparation.TryGetValue(entry.Id, out var previous) && previous == certificate) continue;
            try { EnsureSceneDocumentView(entry); failedSceneViewPreparation.Remove(entry.Id); }
            catch (Exception error)
            {
                EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                failedSceneViewPreparation[entry.Id] = certificate;
                statusMessage = error.Message;
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Scene", error.ToString());
            }
        }
    }

    private void SetSceneDocumentVisibility(EditorDocumentGroup group, EditorDocumentEntry? entry, bool visible)
    {
        if (visibleSceneDocuments.TryGetValue(group.Id, out var previous) && (!visible || !ReferenceEquals(previous, entry)))
        {
            if (sceneDocumentViews.TryGetValue(previous.Id, out var old))
            { CancelSceneDocumentCameraNavigation(old); old.Viewport.Visible = false; }
            visibleSceneDocuments.Remove(group.Id);
        }
        if (visible && entry != null && CanPresentSceneDocument(entry))
        { visibleSceneDocuments[group.Id] = entry; sceneDocumentViews[entry.Id].Viewport.Visible = true; }
    }

    private void DrawSceneDocumentContents(EditorDocumentGroup group, EditorDocumentEntry entry, bool focused)
    {
        if (!CanPresentSceneDocument(entry)) return;
        var view = sceneDocumentViews[entry.Id];
        if (!HasSceneContentArea())
        {
            CancelSceneDocumentCameraNavigation(view);
            view.Viewport.Visible = false;
            return;
        }
        if (ReferenceEquals(documents.Active, entry) && ReferenceEquals(document, entry.Document)
            && ReferenceEquals(engineViewport, view.Viewport)) { DrawSceneContents(view.Viewport); return; }
        Gui.PushID(entry.Id.ToString());
        try
        {
            DrawSceneToolbar(GetSceneToolbarContext(view));
            if (!IsCurrentSceneDocumentView(view)) return;
            var size = Gui.GetContentRegionAvail();
            if (size.X <= 0 || size.Y <= 0) { CancelSceneDocumentCameraNavigation(view); view.Viewport.Visible = false; return; }
            view.Viewport.Resize((int)size.X, (int)size.Y);
            var origin = Gui.GetCursorScreenPos();
            Gui.Image(view.Viewport.TextureRef, size);
            Gui.SetCursorScreenPos(origin);
            Gui.InvisibleButton("Scene viewport input", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
            RegisterVisualCaptureItem("scene.viewport", "viewport", "Scene viewport");
            HandleSceneDocumentCameraInput(GetSceneToolbarContext(view), size);
        }
        finally { Gui.PopID(); }
    }

    private void UpdateSceneDocumentViews(GameTime time)
    {
        PrepareSceneDocumentViews();
        RestoreCurrentDocumentGroupLayout();
        foreach (var view in sceneDocumentViews.Values)
        {
            if (!IsCurrentSceneDocumentView(view)) continue;
            SynchronizeAndUpdateSceneDocumentView(view, time);
        }
        PumpSceneDocumentViewDrain();
    }
    private void RenderSceneDocumentViews(GameTime time)
    {
        if (IsEditorGpuRetirementFinalizing || IsEditorGraphicsRecovering) return;
        foreach (var view in sceneDocumentViews.Values)
            if (IsCurrentSceneDocumentView(view) && view.Viewport.Visible) RenderSceneDocumentView(view, time);
    }

    private void ReleaseSceneDocumentView(EditorDocumentEntry entry, bool persistViewSettings = true)
    {
        if (!sceneDocumentViews.TryGetValue(entry.Id, out var view)) return;
        if (persistViewSettings) SaveSceneDocumentViewControlSettings(view);
        ForgetSceneViewSynchronization(view.Viewport);
        ForgetSceneCompositorView(view.Viewport);
        sceneDocumentViews.Remove(entry.Id);
        CancelSceneDocumentCameraNavigation(view);
        view.Viewport.Visible = false;
        drainingSceneDocumentViews.Add(view);
        retiredSceneDocumentBorrowers.Add(view);
        if (ReferenceEquals(engineViewport, view.Viewport)) engineViewport = null;
        RetireEditorPreview(view);
        view.Viewport.BeginSceneRetirementDrain();
    }
    private void BeginSceneDocumentViewDrain()
    {
        foreach (var view in sceneDocumentViews.Values)
        { CancelSceneDocumentCameraNavigation(view); view.Viewport.Visible = false; drainingSceneDocumentViews.Add(view); view.Viewport.BeginSceneRetirementDrain(); }
    }
    private bool IsSceneDocumentViewDraining => drainingSceneDocumentViews.Count != 0;
    private void PumpSceneDocumentViewDrain()
    {
        foreach (var view in drainingSceneDocumentViews.ToArray())
            if (view.Viewport.PumpSceneRetirementDrain()) drainingSceneDocumentViews.Remove(view);
        retiredSceneDocumentBorrowers.RemoveWhere(static view => view.Viewport.IsResourceRetirementComplete);
    }
    private void ClearSceneDocumentViews(bool persistViewSettings = true)
    {
        foreach (var view in sceneDocumentViews.Values.ToArray()) ReleaseSceneDocumentView(view.Entry, persistViewSettings);
        visibleSceneDocuments.Clear();
        failedSceneViewPreparation.Clear();
    }

    private IEnumerable<EngineViewport> LiveSceneDocumentViewports => sceneDocumentViews.Values.Select(view => view.Viewport);
    private void ReplayAllSceneDocumentResources()
    {
        foreach (var view in sceneDocumentViews.Values)
            if (IsCurrentSceneDocumentView(view)) ReplaySceneDocumentResources(view);
    }
    private void AdoptRecreatedSceneDocumentView(EngineViewport previous, EngineViewport replacement)
    {
        var view = sceneDocumentViews.Values.FirstOrDefault(view => ReferenceEquals(view.Viewport, previous));
        if (view == null) return;
        ForgetSceneViewSynchronization(previous);
        if (!ReferenceEquals(replacement.PresentedDocument, view.Document) || replacement.SourceContent == null
            || projectContent == null || !IsCurrentDocumentPresentation(view.Entry)
            || !ReferenceEquals(view.Entry.Document, view.Document) || !ReferenceEquals(view.Entry.State, view.State))
            throw new InvalidOperationException("The recreated scene view does not match its current canonical owner.");
        var next = new SceneDocumentView { Entry = view.Entry, Document = view.Document, State = view.Entry.State!,
            Workspace = workspace!, ProjectContent = projectContent, Content = replacement.SourceContent, Viewport = replacement };
        ReplaySceneDocumentResources(next);
        replacement.Visible = previous.Visible;
        AdoptPreparedSceneDocumentView(view, next);
    }
    private void AdoptPreparedSceneDocumentView(SceneDocumentView view, SceneDocumentView next)
    {
        if (!sceneDocumentViews.TryGetValue(view.Entry.Id, out var current) || !ReferenceEquals(current, view)
            || !IsCurrentSceneDocumentView(view) || !ReferenceEquals(next.Entry, view.Entry)
            || !ReferenceEquals(next.Document, view.Document) || !ReferenceEquals(next.State, view.State)
            || !ReferenceEquals(next.Workspace, workspace) || !IsCurrentOrRetainedSceneProjectContent(next.ProjectContent)
            || !ReferenceEquals(next.Viewport.PresentedDocument, view.Document)
            || !ReferenceEquals(next.Content, next.Viewport.SourceContent))
            throw new InvalidOperationException("The prepared native scene does not match its current canonical view.");
        next.LoadedPolicy = candidate => IsSceneDocumentViewLoaded(next.Viewport, candidate);
        next.Viewport.SceneLoaded = next.LoadedPolicy;
        TransferSceneDocumentControlContext(view, next);
        sceneDocumentViews[view.Entry.Id] = next;
        ForgetSceneViewSynchronization(view.Viewport);
        ForgetSceneCompositorView(view.Viewport);
        // The caller also retains its old viewport; this owner keeps exact borrowed
        // import leases until that same native viewport's phased disposal succeeds.
        drainingSceneDocumentViews.Add(view); retiredSceneDocumentBorrowers.Add(view); RetireEditorPreview(view); view.Viewport.BeginSceneRetirementDrain();
    }
}
