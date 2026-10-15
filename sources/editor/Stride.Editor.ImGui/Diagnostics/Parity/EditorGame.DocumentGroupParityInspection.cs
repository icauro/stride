using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed record DocumentGroupParityView(EditorDocumentEntry Entry, IEditorDocument Document,
    EditorAuthoringState? State, Guid? Generation, Guid GroupId, Guid[] GroupItems, bool Floating,
    uint DockId, uint RootDockId, uint ParentDockId, int ParentSplitAxis, bool MainRoot,
    uint ViewportId, long NativeWindow, NVector2 Position, NVector2 Size, bool NativeActive,
    EngineViewport? View, object? Runtime, object? Compositor, Matrix Camera, bool Visible, long RenderRevision, object? NativeTabs, Guid? GroupActiveId);

internal sealed partial class EditorGame
{
    internal object? InspectNativeDockSelectionRestore()
        => Controls.Enabled ? nativeDockSelectionRestore.Inspection : null;
    internal unsafe object InspectDocumentGroupWorkArea()
    {
        var viewport = Gui.GetMainViewport();
        var menu = ImGuiP.FindWindowByName("##MainMenuBar");
        var roots = new Dictionary<uint, object>();
        foreach (var group in DocumentGroupPanel.Model.Groups)
        {
            var window = ImGuiP.FindWindowByName(group.WindowName);
            if (window.IsNull || window.DockId == 0) continue;
            var root = ImGuiP.DockBuilderGetNode(window.DockId).Handle;
            for (var depth = 0; root != null && root->ParentNode != null; depth++)
            {
                if (depth >= 128) throw new InvalidOperationException("Native document docking ancestry is invalid.");
                root = root->ParentNode;
            }
            if (root == null || roots.ContainsKey(root->ID)) continue;
            roots.Add(root->ID, new { id = root->ID, position = new { X = root->Pos.X, Y = root->Pos.Y },
                size = new { X = root->Size.X, Y = root->Size.Y }, flags = root->MergedFlags.ToString(),
                hostWindow = root->HostWindow == null ? 0 : root->HostWindow->ID });
        }
        return new { frame = Gui.GetFrameCount(), mainViewportId = viewport.ID,
            position = new { viewport.Pos.X, viewport.Pos.Y }, size = new { viewport.Size.X, viewport.Size.Y },
            workPosition = new { viewport.WorkPos.X, viewport.WorkPos.Y }, workSize = new { viewport.WorkSize.X, viewport.WorkSize.Y },
            menuWindowId = menu.IsNull ? 0 : menu.ID, menuActive = !menu.IsNull && menu.WasActive && !menu.Hidden,
            menuFrame = menu.IsNull ? -1 : menu.LastFrameActive,
            menuPosition = menu.IsNull ? null : new { menu.Pos.X, menu.Pos.Y },
            menuSize = menu.IsNull ? null : new { menu.Size.X, menu.Size.Y }, nativeRoots = roots.Values.ToArray() };
    }

    internal unsafe DocumentGroupParityView? InspectDocumentGroupParity(Guid documentId)
    {
        var entry = documents.Entries.FirstOrDefault(entry => entry.Id == documentId);
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        if (entry == null || group == null) return null;
        var window = ImGuiP.FindWindowByName(group.WindowName);
        var dock = window.IsNull ? 0 : window.DockId;
        var node = dock == 0 ? null : ImGuiP.DockBuilderGetNode(dock).Handle;
        var root = node;
        for (var depth = 0; root != null && root->ParentNode != null; depth++)
        {
            if (depth >= 128) throw new InvalidOperationException("Native document docking ancestry is invalid.");
            root = root->ParentNode;
        }
        var viewport = window.IsNull ? default(ImGuiViewportPtr) : Gui.FindViewportByID(window.ViewportId);
        var view = entry.Document is EntityHierarchyDocument hierarchy ? VerificationDocumentViewport(hierarchy) : null;
        return new(entry, entry.Document, entry.State, entry.State?.Generation, group.Id,
            group.Items.Select(item => item.Id).ToArray(), group.Floating, dock, root == null ? 0 : root->ID,
            node == null || node->ParentNode == null ? 0 : node->ParentNode->ID,
            node == null || node->ParentNode == null ? -1 : (int)node->ParentNode->SplitAxis,
            root != null && (root->MergedFlags & (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space) != 0,
            viewport.IsNull ? 0 : viewport.ID, viewport.IsNull ? 0 : (long)(nint)viewport.PlatformHandleRaw,
            window.IsNull ? NVector2.Zero : window.Pos, window.IsNull ? NVector2.Zero : window.Size,
            !window.IsNull && window.WasActive && !window.Hidden,
            view, view?.RuntimeScene, view?.VerificationCompositorOwner,
            view?.Camera.Component.ViewMatrix ?? Matrix.Identity, view?.Visible == true, view?.VerificationRenderRevision ?? 0,
            DocumentGroupPanel.InspectNativeTabs(group.Id), group.Active?.Id);
    }

    internal bool DocumentGroupParityCaseQuiescent => !IsAuthoringPipelineUpdating && !IsAssetPreviewUpdating
        && projectLoading == null && !IsEditorGpuRetirementPending && !IsEditorGraphicsRecovering;
}
