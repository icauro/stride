using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private sealed class ModalRequest(ProjectWorkspace workspace, Type type, Action<object?> commit, ContentReferenceEditCertificate certificate,
        AssetId? initialAsset, string? initialFolder, bool forceAssignment)
    {
        internal ProjectWorkspace Workspace { get; } = workspace;
        internal Type Type { get; } = type;
        internal Action<object?> Commit { get; } = commit;
        internal ContentReferenceEditCertificate Certificate { get; } = certificate;
        internal AssetId? InitialAsset { get; } = initialAsset;
        internal string? InitialFolder { get; } = initialFolder;
        internal bool ForceAssignment { get; } = forceAssignment;
        internal Type[]? TemplateAcceptedTypes { get; init; }
        internal Func<AssetItem, bool>? TemplatePick { get; init; }
        internal Action? TemplateCancel { get; init; }
        internal string? TemplateMessage { get; init; }
        internal bool SourceImportAllowed { get; init; } = true;
        internal bool TemplateFinished;
        internal bool ContextMutationPending { get; set; }
        internal bool IsCurrent() => Certificate.IsCurrent();
    }
    private ModalRequest? modal;
    internal bool HasModalRequest => modal != null;
    private readonly AssetBrowserState modalBrowser = new();
    private readonly HashSet<string> expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string modalSearch = "";
    private readonly AssetFilterSearch modalFilterSearch = new();
    private static readonly AssetFilterCategory[] modalFilterCategories = [AssetFilterCategory.Name, AssetFilterCategory.Tag];
    private readonly HashSet<string> modalRequiredTypeFilters = new(StringComparer.Ordinal);
    private bool modalRequested;
    private string ModalTitle => EditorLocalization.Text("Select an asset") + "###" + popupIdentity;

    private void RequestModal(Type type, object? value, Action<object?> commit, bool mixed, bool forceAssignment)
    {
        if (host.Workspace is not { } workspace) return;
        if (modal?.ContextMutationPending == true) return;
        CancelModal();
        modal = new(workspace, type, commit, host.CaptureReferenceEditCertificate(true), mixed ? null : ContentAssetReference.Read(value)?.Id,
            host.InitialFolderId, forceAssignment);
        modalRequested = true;
    }

    internal void CancelModal()
    {
        DrainPickerTransferCertificates();
        host.CancelPickerTransfers(modalBrowser);
        pickerImportBoundsVisible = pickerTransferClose = false;
        modal?.Certificate.Dispose();
        if (modal is { TemplateFinished: false, TemplateCancel: not null } canceled)
        { canceled.TemplateFinished = true; canceled.TemplateCancel(); }
        modal = null; modalRequested = false; modalBrowser.Bind(null); modalSearch = "";
        referenceDrafts.Clear();
        modalFilterSearch.Reset(); modalTextSearch.Reset(); modalRequiredTypeFilters.Clear(); modalTileLabels.Clear(); modalTileItemIds.Clear();
        contextSuspended = false; contextWaitFrames = 0; renamingContent = null;
        modalLocationAnchor = null; modalClickedLocation = null; modalVisibleLocations.Clear();
        modalLocationItemIds.Clear(); modalLocationFocusTarget = modalLocationExpansionTarget = null;
    }

    internal void DrawModal()
    {
        DrainPickerTransferCertificates();
        pickerImportBoundsVisible = false;
        if (ClosePickerTransferPopup()) return;
        modalFilterSearch.BeginFrame();
        modalLocationConsumedEscape = false;
        var title = ModalTitle;
        renameEscapeHandled = false;
        if (contextSuspended)
        {
            if (modal?.ContextMutationPending == true) return;
            if (contextWaitFrames > 0) { contextWaitFrames--; return; }
            if (host.ContextCommandPending || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
            contextSuspended = false;
            if (modal is not { } suspended || !suspended.IsCurrent()) { CancelModal(); return; }
            modalBrowser.RefreshEntries(suspended.Workspace); Gui.OpenPopup(title);
        }
        if (modalRequested && modal is { } pending)
        {
            modalRequested = false; modalSearch = "";
            modalBrowser.Bind(null); modalBrowser.BindPicker(pending.Workspace);
            foreach (var category in modalBrowser.Categories.Where(category => category.Kind == AssetBrowserCategoryKind.Local)) expandedFolders.Add(category.Id);
            modalFilterSearch.Reset(); modalRequiredTypeFilters.Clear();
            modalBrowser.RestoreFilters(host.SavedPickerFilters);
            foreach (var type in ModalAssetTypes(pending))
            {
                modalRequiredTypeFilters.Add(type.FullName!);
                modalBrowser.CommitFilter(new(AssetFilterCategory.Type, Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name, type), false);
            }
            var view = host.PickerViewSettings;
            modalBrowser.DisplayMode = view.Display; modalBrowser.Sort = view.Sort; modalThumbnailSize = Math.Clamp(view.ThumbnailSize, 16, 128);
            modalTileLabels.Clear(); modalTileItemIds.Clear();
            if (pending.InitialAsset is { } initial && host.FindReferenceAsset(initial) != null)
                modalBrowser.RevealAsset(pending.Workspace, initial);
            else
            {
                var folder = pending.InitialFolder == null ? null : modalBrowser.FindFolder(pending.InitialFolder);
                folder ??= modalBrowser.Packages.FirstOrDefault(root => !root.Package.IsReadOnly)?.Children.FirstOrDefault(child => child.Name == "Assets");
                if (folder != null) modalBrowser.SelectFolder(folder);
            }
            modalLocationAnchor = modalBrowser.SelectedFolder?.Id;
            for (var location = modalBrowser.SelectedFolder is { } initialLocationFolder ? modalBrowser.Location(initialLocationFolder) : null;
                location != null; location = modalBrowser.ParentLocation(location)) expandedFolders.Add(location.Id);
            Gui.OpenPopup(title);
            var dpi = Gui.GetStyle().FontScaleDpi; var work = Gui.GetMainViewport().WorkSize;
            Gui.SetNextWindowSize(new Vector2(Math.Min(1024 * dpi, work.X), Math.Min(768 * dpi, work.Y)), ImGuiCond.Appearing);
        }
        using var popup = ImGuiScopes.Modal(title, ImGuiWindowFlags.NoSavedSettings);
        if (!popup.DrawContents) return;
        if (modal is not { } request || !ReferenceEquals(host.Workspace, request.Workspace) || !request.IsCurrent())
        { Gui.CloseCurrentPopup(); CancelModal(); return; }
        modalBrowser.BindPicker(request.Workspace);
        var choices = (request.TemplateAcceptedTypes == null ? host.Choices(request.Type)
            : request.Workspace.AssetItems.Where(item => request.TemplateAcceptedTypes.Any(type => type.IsInstanceOfType(item.Asset)))
                .Select(item => new ContentReferenceChoice(item, item.Location.ToString())).ToArray())
            .Where(choice => !choice.Asset.IsDeleted).ToDictionary(choice => choice.Asset.Id);
        if (!string.IsNullOrEmpty(request.TemplateMessage)) host.DrawSelectableText("##TemplatePickerMessage", request.TemplateMessage);
        var dpiScale = Gui.GetStyle().FontScaleDpi;
        var bodyHeight = Math.Max(1, Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing() - 40 * dpiScale);
        if (Gui.BeginTable("##PickerLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV, new Vector2(0, bodyHeight)))
        {
            try
            {
                Gui.TableSetupColumn("##Locations", ImGuiTableColumnFlags.WidthStretch, 1f);
                Gui.TableSetupColumn("##Assets", ImGuiTableColumnFlags.WidthStretch, 2f);
                Gui.TableNextColumn();
                using (var tree = ImGuiScopes.Child("##Locations", new Vector2(0, bodyHeight), ImGuiChildFlags.None, ImGuiWindowFlags.None))
                    if (tree.DrawContents)
                    {
                        BeginPickerLocations(request);
                        foreach (var category in modalBrowser.Categories) DrawPickerLocation(modalBrowser.Location(category));
                    }
                ApplyPickerLocationClick(request);
                Gui.TableNextColumn();
                using var content = ImGuiScopes.Child("##Assets", new Vector2(0, bodyHeight), ImGuiChildFlags.None, ImGuiWindowFlags.None);
                if (content.DrawContents)
                {
                    RecordPickerImportBounds();
                    DrawPickerContextShortcuts(request);
                    DrawPickerFilterToolbar();
                    if (DrawPickerTiles(request, choices)) return;
                    if (Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) pickerImportBoundsVisible = false;
                }
            }
            finally { Gui.EndTable(); }
        }
        if (contextSuspended) { Gui.CloseCurrentPopup(); return; }
        var blockPickerKeys = Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        if (blockPickerKeys) pickerImportBoundsVisible = false;
        var ownsCancelKey = !blockPickerKeys && !Gui.GetIO().AppFocusLost
            && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        using var blockedFooter = ImGuiScopes.Disabled(blockPickerKeys);
        var buttonSize = new Vector2(80 * dpiScale, 0);
        Gui.SetCursorPosX(Math.Max(Gui.GetCursorPosX(), Gui.GetWindowWidth() - 180 * dpiScale));
        using (ImGuiScopes.Disabled(modalBrowser.SelectedContent.FirstOrDefault(row => row.Asset != null)?.Asset?.Id is not { } chosen || !choices.ContainsKey(chosen)))
            if (Gui.Button(EditorLocalization.Widget("OK", "Button"), buttonSize))
                if (TryAccept(request, choices)) return;
        RecordControl?.Invoke("template/reference/accept");
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Select these assets", "ToolTip"));
        Gui.SameLine();
        var cancelPressed = ownsCancelKey && !renameEscapeHandled && !modalLocationConsumedEscape && !modalFilterSearch.ConsumedEscape
            && !Gui.GetIO().WantTextInput && Gui.IsKeyPressed(ImGuiKey.Escape, false);
        var cancelClicked = Gui.Button(EditorLocalization.Widget("Cancel", "Button"), buttonSize);
        RecordControl?.Invoke("template/reference/cancel");
        if (cancelClicked || cancelPressed)
        {
            if (cancelPressed) ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetID("PickerCancel"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            Gui.CloseCurrentPopup(); CancelModal();
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Cancel (Esc)", "ToolTip"));
    }

    private bool TryAccept(ModalRequest request, IReadOnlyDictionary<AssetId, ContentReferenceChoice> choices)
    {
        if (!request.IsCurrent() || modalBrowser.SelectedContent.FirstOrDefault(row => row.Asset != null)?.Asset?.Id is not { } id || !choices.TryGetValue(id, out var choice)) return false;
        var current = host.Workspace?.AssetItems.FirstOrDefault(item => item.Id == id && !item.IsDeleted);
        if (current == null) return false;
        if (request.TemplateAcceptedTypes != null)
        {
            if (!request.TemplateAcceptedTypes.Any(type => type.IsInstanceOfType(current.Asset)) || request.TemplatePick?.Invoke(current) != true) return false;
            request.TemplateFinished = true;
        }
        else
        {
            if (!ContentAssetReference.Accepts(request.Type, current)) return false;
            if (!host.AssignPicked(request.Type, current.Id, request.IsCurrent, request.Commit)) return false;
        }
        Gui.CloseCurrentPopup(); CancelModal(); return true;
    }

    private static Type[] ModalAssetTypes(ModalRequest request) => request.TemplateAcceptedTypes == null ? PickerAssetTypes(request.Type)
        : AssetRegistry.GetPublicTypes().Where(type => request.TemplateAcceptedTypes.Any(accepted => accepted.IsAssignableFrom(type)))
            .OrderBy(Stride.Core.DisplayAttribute.GetDisplayName).ToArray();

    internal void RequestTemplateReference(ProjectWorkspace workspace, Type[] acceptedTypes, AssetId? initialAsset, string? initialFolder,
        ContentReferenceEditCertificate certificate, Func<AssetItem, bool> pick, Action canceled, string message)
    {
        if (modal?.ContextMutationPending == true) throw new InvalidOperationException("The asset picker is busy.");
        CancelModal();
        modal = new(workspace, typeof(object), _ => { }, certificate, initialAsset, initialFolder, false)
        { TemplateAcceptedTypes = acceptedTypes.ToArray(), TemplatePick = pick, TemplateCancel = canceled, TemplateMessage = message, SourceImportAllowed = true };
        modalRequested = true;
    }

    private static Type[] PickerAssetTypes(Type contentType)
    {
        if (!AssetRegistry.CanPropertyHandleAssets(contentType, out var accepted)) return [];
        return AssetRegistry.GetPublicTypes().Where(type => accepted.Any(candidate => candidate.IsAssignableFrom(type)))
            .OrderBy(Stride.Core.DisplayAttribute.GetDisplayName).ToArray();
    }

    private void DrawPickerLocation(AssetBrowserLocation location)
    {
        modalVisibleLocations.Add(location);
        Gui.PushID(location.Id);
        try
        {
            Gui.SetNextItemOpen(expandedFolders.Contains(location.Id), ImGuiCond.Once);
            var children = modalBrowser.LocationChildren(location);
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
            if (modalBrowser.IsLocationSelected(location)) flags |= ImGuiTreeNodeFlags.Selected;
            if (children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
            PreparePickerLocationNode(location);
            var open = host.DrawPickerFolderNode(location.Name, flags, out var virtualized);
            CompletePickerLocationNode(location, virtualized);
            if (!virtualized && Gui.IsItemClicked() && !Gui.IsItemToggledOpen()) CapturePickerLocationClick(location);
            if (open)
            {
                expandedFolders.Add(location.Id);
                try { foreach (var child in children) DrawPickerLocation(child); }
                finally { Gui.TreePop(); }
            }
            else expandedFolders.Remove(location.Id);
        }
        finally { Gui.PopID(); }
    }

}
