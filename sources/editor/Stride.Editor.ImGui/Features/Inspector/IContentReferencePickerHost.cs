using Stride.Core.Assets;
using System.Numerics;
using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record ContentReferenceChoice(AssetItem Asset, string Label);
internal sealed record AssetPickerViewSettings(AssetBrowserSort Sort, AssetBrowserDisplayMode Display, int ThumbnailSize);
internal delegate void PickerAuthoringCommand(Action<bool> completed);
internal readonly record struct PickerTileBadges(Action? Command, bool Hovered);

internal interface IContentReferencePickerHost
{
    ProjectWorkspace? Workspace { get; }
    bool HasThumbnails { get; }
    bool HasDynamicReferenceThumbnail(Type propertyType);
    string AssetPayloadType { get; }
    IReadOnlyList<ContentReferenceChoice> Choices(Type type);
    AssetItem? FindReferenceAsset(string url);
    AssetItem? FindReferenceAsset(AssetId id);
    AssetEntry? FindReferenceEntry(AssetId id);
    string ReferenceDisplayUrl(AssetItem item);
    void RenderThumbnail(AssetEntry entry, Vector2 position, float size);
    bool TryResolveDrop(nint data, int size, Type type, out AssetItem? asset);
    void Assign(Type type, AssetItem? asset, Action<object?> commit);
    void AssignValue(object? value, Action<object?> commit);
    string? InitialFolderId { get; }
    Func<bool> CaptureSelectionGuard();
    Func<bool> CaptureReferenceEditGuard(bool includeRevisions = true);
    ContentReferenceEditCertificate CaptureReferenceEditCertificate(bool retainOwners = false);
    bool AssignPicked(Type type, AssetId id, Func<bool> isCurrent, Action<object?> commit);
    bool DrawPickupButton();
    bool DrawFetchButton();
    bool DrawReferenceMenuItem(string label, bool enabled);
    bool CanFetch(object? value);
    void Fetch(object? value);
    PickerContextCapabilities ContextCapabilities(AssetBrowserContent content);
    Action? CaptureBackgroundLocationExplore();
    void RunContextAction(AssetBrowserContent content, PickerContextAction action, AssetBrowserState selection, Func<bool> isCurrent, string? name = null, Action<bool>? completed = null);
    bool ContextCommandPending { get; }
    void DrawPickerDragSource(AssetBrowserContent content, AssetBrowserState browser, Func<bool> isCurrent);
    PickerAuthoringCommand? DrawPickerDropTarget(AssetBrowserFolder folder, Func<bool> isCurrent);
    void RequestPickerSourceImport(string[] files, AssetBrowserFolder folder, AssetBrowserState browser,
        ContentReferenceEditCertificate retention, Func<bool> isCurrent, Action<bool> completed);
    void CancelPickerTransfers(AssetBrowserState browser);
    void DrawSelectableText(string id, string text);
    IReadOnlyList<AssetFilterPreference> SavedPickerFilters { get; }
    void SavePickerFilters(IReadOnlyList<AssetFilterPreference> filters);
    AssetPickerViewSettings PickerViewSettings { get; }
    EditorIcons? PickerIcons { get; }
    AssetSortMetadata PickerSortMetadata { get; }
    void SyncPickerSortMetadata();
    int SavePickerThumbnailSize(int size);
    void RefreshPickerTileStatus();
    PickerTileBadges DrawPickerTileBadges(AssetEntry asset, Vector2 origin, float thumbnailSize, Func<bool> isCurrent);
    void DrawPickerTooltip(AssetBrowserContent content);
    Vector2 MeasurePickerLabel(string text);
    void PaintPickerLabel(string text, Vector2 origin, Vector2 minimum, Vector2 maximum, uint color);
    void DrawPickerLabel(string text);
    bool DrawPickerFolderNode(string name, ImGuiTreeNodeFlags flags, out bool virtualized);
}
