using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using SVector3 = Stride.Core.Mathematics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawMainMenu(EditorIcons icons)
    {
        if (BeginNativeMainMenuBar())
        {
            try
            {
                var fileMenuOpen = BeginNativeMenu(EditorLocalization.Widget("File", "Menu"));
                Controls.Record("menu/file");
                if (fileMenuOpen)
                {
                    try
                    {
                        if (DrawNativeMenuItem(EditorLocalization.Widget("New", "Menu"))) ProjectCreation.Open();
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Open", "Menu"), "Ctrl+O")) showOpenProject = true;
                        DrawRecentProjectsMenu();
                        if (DrawNativeMenuItem("Recover changes", enabled: CanRequestRecoveryDialog)) RequestRecoveryDialog();
                        Controls.Record("menu/file/recover-changes");
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Save", "Menu"), "Ctrl+S", false,
                            !IsPackagePlatformUpdating && (documents.HasDirtyDocuments || HasPendingRenameChanges || HasPendingPropertyStrings || workspace?.HasPackageChanges == true))) Execute(() => SaveAllDocuments(() => { }));
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Settings", "Menu"))) OpenEditorSettings();
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Exit", "Menu"), "Alt+F4")) RequestEditorClose();
                    }
                    finally { Gui.EndMenu(); }
                }
                if (BeginNativeMenu(EditorLocalization.Widget("Edit", "Menu")))
                {
                    try
                    {
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Undo", "Menu"), "Ctrl+Z", false, CanUndoCurrent || HasPendingRenameChanges)) Execute(UndoCurrentCommand);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Redo", "Menu"), "Ctrl+Y", false, CanRedoCurrent)) Execute(RedoCurrentCommand);
                        Gui.Separator();
                        if (CommandDocument is UIDocument uiDocument && !IsCurrentAssetPropertyContext)
                        {
                            var selected = ownedUIEditor?.Selection.Count > 0;
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, selected && !Gui.GetIO().WantTextInput)) Execute(() => TryUIEditorClipboardCommand("copy"));
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, selected && uiDocument.CanSave && !Gui.GetIO().WantTextInput)) Execute(() => TryUIEditorClipboardCommand("cut"));
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, uiDocument.CanSave && !Gui.GetIO().WantTextInput)) Execute(() => TryUIEditorClipboardCommand("paste"));
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Duplicate", "Menu"), "Ctrl+D", false, selected && uiDocument.CanSave && !Gui.GetIO().WantTextInput)) Execute(() => TryUIEditorCommand("duplicate"));
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, selected && uiDocument.CanSave && !Gui.GetIO().WantTextInput)) Execute(() => TryUIEditorCommand("delete"));
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, ownedUIEditor?.Selection.Count == 1 && uiDocument.CanSave && !Gui.GetIO().WantTextInput)) Execute(() => TryUIEditorCommand("rename"));
                        }
                        else
                        {
                            using (ImGuiScopes.Disabled(CommandDocument is not EntityHierarchyDocument || !ReferenceEquals(CommandDocument, document) || Gui.GetIO().WantTextInput))
                            {
                                DrawHierarchyClipboardMenu();
                            }
                        }
                    }
                    finally { Gui.EndMenu(); }
                }
                DrawProjectMenu();
                var viewMenuOpen = BeginNativeMenu(EditorLocalization.Widget("View", "Menu"));
                if (Controls.Enabled) Controls.Record("menu/view");
                if (viewMenuOpen)
                {
                    try
                    {
                        DrawNativeMenuItem(EditorLocalization.Widget("Hierarchy", "Menu"), "", ref showHierarchy);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Solution explorer", "Menu"), "", ref showSolutionExplorer) && showSolutionExplorer) SolutionExplorerController.FocusRequested = true;
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Asset view", "Menu"), "", ref showAssets) && showAssets) focusAssetsPanel = true;
                        DrawNativeMenuItem(EditorLocalization.Widget("Property grid", "Menu"), "", ref showInspector);
                        Controls.Record("menu/view/property-grid");
                        DrawNativeMenuItem(EditorLocalization.Widget("Edit history", "Menu"), "", ref showEditHistory);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Output", "Menu"), "", ref showOutput) && showOutput) focusOutput = true;
                        DrawNativeMenuItem(EditorLocalization.Widget("References", "Menu"), "", ref showAssetReferences);
                        DrawNativeMenuItem(EditorLocalization.Widget("Asset errors", "Menu"), "", ref showAssetErrors);
                        if (DrawNativeMenuItem("Show scene", "", false, document != null)) Gui.SetWindowFocus("Scene");
                        if (Controls.Enabled) Controls.Record("menu/view/scene");
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Asset preview", "Menu"), "", ref showAssetPreview)) focusAssetPreview = showAssetPreview;
                        if (Controls.Enabled) Controls.Record("menu/view/asset-preview");
                        DrawNativeMenuItem("Dear ImGui demo", "", ref showDemo);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset layout", "Menu"))) { resetLayout = true; ResetPanelVisibility(); }
                        Controls.Record("menu/view/reset-layout");
                    }
                    finally { Gui.EndMenu(); }
                }
                DrawHelpMenu();
                Gui.Separator();
                DrawSelectionNavigation(icons);
                var openProjectClicked = icons.Button("open", EditorLocalization.Text("Open an existing project", "ToolTip"));
                TraceWorkflowUiAction("toolbar/open", openProjectClicked);
                if (openProjectClicked) showOpenProject = true;
                if (Controls.Enabled) Controls.Record("toolbar/open");
                var saveEligible = !IsPackagePlatformUpdating && (documents.HasDirtyDocuments || HasPendingRenameChanges || HasPendingPropertyStrings || workspace?.HasPackageChanges == true);
                using (ImGuiScopes.Disabled(!saveEligible))
                {
                    var savePressed = icons.Button("save", EditorLocalization.Text("Save the project and all its documents", "ToolTip"));
                    RecordAssetTemplateSaveUiReceipt(savePressed, saveEligible);
                    if (savePressed) Execute(() => SaveAllDocuments(() => { }));
                    if (Controls.Enabled) Controls.Record("toolbar/save");
                }
                using (ImGuiScopes.Disabled(!CanUndoCurrent && !HasPendingRenameChanges))
                {
                    var undoPressed = icons.Button("undo", EditorLocalization.Text("Undo last action", "ToolTip"));
                    if (Controls.Enabled) RecordStockHistoryToolbar("undo", undoPressed);
                    if (undoPressed) Execute(UndoCurrentCommand);
                    if (Controls.Enabled) Controls.Record("toolbar/undo");
                }
                using (ImGuiScopes.Disabled(!CanRedoCurrent))
                {
                    var redoPressed = icons.Button("redo", EditorLocalization.Text("Redo last cancelled action", "ToolTip"));
                    if (Controls.Enabled) RecordStockHistoryToolbar("redo", redoPressed);
                    if (redoPressed) Execute(RedoCurrentCommand);
                    if (Controls.Enabled) Controls.Record("toolbar/redo");
                }
                using (ImGuiScopes.Disabled(workspace == null || IsProjectLoading || IsAuthoringPipelineUpdating || IsUserProjectBuilding))
                {
                    if (icons.Button("reload", EditorLocalization.Text("Reload current project (ask to save)", "ToolTip"))) RequestReloadProject();
                    if (Controls.Enabled) Controls.Record("toolbar/reload");
                }
            }
            finally { EndNativeMainMenuBar(); }
        }
    }

    private void HandleEditorShortcuts()
    {
        ProcessAssetSourceUpdateShortcuts();
        ProcessAssetBrowserEditShortcut();
        ProcessAssetBrowserAddShortcut();
        if (IsUserProjectBuilding && !closeRequested && !closePromptOpen && closingDocument == null
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyShift && !Gui.GetIO().KeyAlt && Gui.IsKeyPressed(ImGuiKey.F6, false))
            Execute(CancelUserProjectBuild);
        if (projectLoading == null && !RecoveryTransitionBusy && !closeRequested && !closePromptOpen && closingDocument == null
            && Gui.GetIO().KeyCtrl && Gui.GetIO().KeyShift && Gui.IsKeyPressed(ImGuiKey.D, false)) OpenDebugWindow();
        if (projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !closeRequested && !closePromptOpen && closingDocument == null)
        {
            if (Gui.GetIO().KeyCtrl && Gui.GetIO().KeyShift && Gui.IsKeyPressed(ImGuiKey.B, false) && CanStartUserProjectBuild)
                Execute(() => StartUserProjectBuild(false));
            if (!Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyShift && !Gui.GetIO().KeyAlt
                && Gui.IsKeyPressed(ImGuiKey.F6, false) && CanStartUserProjectBuild)
                Execute(() => StartUserProjectBuild(false));
            if (Gui.IsKeyPressed(ImGuiKey.F5, false) && CanStartUserProjectBuild && CanRunUserProject)
                Execute(() => StartUserProjectBuild(true));
        }
        if (projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating
            && (!assetEditorFocused || CommandDocument is AssetMetadataDocument) && !closeRequested && !closePromptOpen
            && closingDocument == null && Gui.GetIO().KeyCtrl && workspace != null && Gui.IsKeyPressed(ImGuiKey.S, false))
            Execute(() => SaveAllDocuments(() => { }));
        if (projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && (!assetEditorFocused || CommandDocument is AssetMetadataDocument) && !closeRequested && !closePromptOpen && closingDocument == null && Gui.GetIO().KeyCtrl && !Gui.IsAnyItemActive())
        {
            if (CommandDocument is UIDocument && assetEditorFocused && !Gui.GetIO().WantTextInput
                && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            {
                if (Gui.IsKeyPressed(ImGuiKey.C, false)) Execute(() => TryUIEditorClipboardCommand("copy"));
                else if (Gui.IsKeyPressed(ImGuiKey.X, false)) Execute(() => TryUIEditorClipboardCommand("cut"));
                else if (Gui.IsKeyPressed(ImGuiKey.V, false)) Execute(() => TryUIEditorClipboardCommand("paste"));
                else if (!Gui.GetIO().KeyShift && !Gui.GetIO().KeyAlt && Gui.IsKeyPressed(ImGuiKey.D, false)) Execute(() => TryUIEditorCommand("duplicate"));
            }
            if (Gui.IsKeyPressed(ImGuiKey.O)) showOpenProject = true;
            if (Gui.IsKeyPressed(ImGuiKey.Z)) Execute(UndoCurrentCommand);
            if (Gui.IsKeyPressed(ImGuiKey.Y)) Execute(RedoCurrentCommand);
        }
        if (CanStartAssetEdit && CommandDocument is UIDocument && assetEditorFocused && !Gui.GetIO().WantTextInput
            && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeyShift
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            if (Gui.IsKeyPressed(ImGuiKey.Delete, false)) Execute(() => TryUIEditorCommand("delete"));
            else if (Gui.IsKeyPressed(ImGuiKey.F2, false)) Execute(() => TryUIEditorCommand("rename"));
        }
    }
}
