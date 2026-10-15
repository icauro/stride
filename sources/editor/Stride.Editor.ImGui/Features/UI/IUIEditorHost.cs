using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record UIElementFactory(string Name, UIDocument Library, Guid ElementId, string Url, bool SystemLibrary)
{
    internal string Category => SystemLibrary
        ? Stride.Core.Reflection.TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<Stride.Core.DisplayAttribute>(
            (Library.Find(ElementId) ?? throw new InvalidOperationException("The corresponding UI element could not be found in the library.")).GetType())?.Category ?? "Misc."
        : Url;
    internal bool IsPanelFactory => SystemLibrary && Category == "Panel" && Library.Find(ElementId) is Stride.UI.Panels.Panel;
}

internal interface IUIEditorHost
{
    IReadOnlyList<UIElementFactory> Factories { get; }
    bool IsCurrentPresentationOwner(UIDocument document);
    UIEditorViewport CreateViewport(UIDocument document);
    long ContentGeneration(UIDocument document);
    UIDocument? ResolveLibrary(Stride.Core.Assets.AssetId id);
    string? LibraryName(Stride.Core.Assets.AssetId id) => null;
    bool DrawElementTreeNode(string label, string name, string type, string? library, int children, bool activeRoot,
        Hexa.NET.ImGui.ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized)
        => DrawTreeNode(name + " [" + type + "]" + (library == null ? "" : "(Library: " + library + ")")
            + (children == 0 ? "" : " (" + children + ")") + "###" + label, flags, forceSubmission, out virtualized);
    void SelectionChanged();
    void SelectionAdded(UIDocument source);
    bool AskBeforeDeletingUIElements => true;
    string UIElementDeletionTitle => "Stride Game Studio###DeleteUIElementsConfirmation";
    void DisableUIElementDeletionConfirmation() { }
    bool DrawConfirmationButton(string label) => Hexa.NET.ImGui.ImGui.Button(label);
    bool DrawConfirmationCheckbox(string label, ref bool value) => Hexa.NET.ImGui.ImGui.Checkbox(label, ref value);
    bool BeginMenu(string label, bool enabled = true) => Hexa.NET.ImGui.ImGui.BeginMenu(label, enabled);
    bool DrawMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true)
        => Hexa.NET.ImGui.ImGui.MenuItem(label, shortcut, selected, enabled);
    bool DrawTreeNode(string label, Hexa.NET.ImGui.ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized)
    {
        virtualized = false;
        return Hexa.NET.ImGui.ImGui.TreeNodeEx(label, flags);
    }
    void ReleaseViewport(UIEditorViewport viewport);
    void RegisterCaptureItem(string id, string kind, string name);
    void RecordDiagnosticControl(string id) { }
    void RecordDiagnosticScrollArea(string id, System.Numerics.Vector2 center) { }
    void DrawSelectableText(string id, string text);
    void DrawStringProperty(UIDocument source, Guid[] ids, string path, bool mixed);
    void DrawRetainedStringProperty(UIDocument source, object identity, bool mixed, Func<bool> canEdit,
        Func<object?[]> read, Action<string> commit, Func<IDisposable>? acquireTarget = null);
    void DrawRetainedNumericProperty(UIDocument source, object identity, Type type, object? value, bool mixed,
        Func<bool> canEdit, Func<object?[]> read, Action<object?> commit,
        Func<PropertyNumericRange?> range, Func<IReadOnlyList<PropertyNumericRange?>> ranges,
        Func<IDisposable>? acquireTarget = null, Func<object?, object?>? formatValue = null);
    void DrawRetainedAngleProperty(UIDocument source, object identity, Func<bool> canEdit,
        Func<object?[]> read, Action<Stride.Core.Mathematics.AngleSingle> commit, int valueCount,
        Func<IDisposable>? acquireTarget = null);
    void DrawRetainedGuidProperty(UIDocument source, object identity, Func<bool> canEdit,
        Func<object?[]> read, Action<Guid> commit, int valueCount, Func<IDisposable>? acquireTarget = null);
    void DrawRetainedTemporalProperty(UIDocument source, object identity, Type type, Func<bool> canEdit,
        Func<object?[]> read, Action<Func<object?, object?>> transform, int valueCount, Func<IDisposable>? acquireTarget = null);
    void DrawRetainedPathProperty(UIDocument source, object identity, Type type, Func<bool> canEdit,
        Func<object?[]> read, Action<object?> commit, int valueCount, Func<IDisposable>? acquireTarget = null);
    void DrawRetainedCharacterProperty(UIDocument source, object identity, Func<bool> canEdit,
        Func<object?[]> read, Action<char> commit, int valueCount, Func<IDisposable>? acquireTarget = null);
    void DrawRetainedColorProperty(UIDocument source, object identity, Type type, Func<bool> canEdit,
        Func<object?[]> read, Action<Func<object?, object?>> transform, int valueCount,
        Func<int, bool>? canEditAxis = null, Func<IDisposable>? acquireTarget = null);
    void DrawReference(Type type, object? value, Action<object?> commit, bool mixed = false, bool allowsNull = true);
    void DrawReferenceFetchActions(IEnumerable<object?> values);
    bool DrawPrimitive(Type type, object? value, string label, bool canEdit, Action<object?> commit, Action endEdit);
    bool DrawEnumImages(UIDocument document, Type type, object? value, bool mixed, Action<object?> commit);
    void DrawDateTime(UIDocument document, DateTime value, bool mixed, bool canEdit, Action<object?> commit);
    bool DrawNullablePresence(bool present, out bool create);
    void DrawCombinedVirtualCommands(UIDocument document, IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter> presenters);
    void SelectUICollectionAddFactoryEntry(UIDocument document, IReadOnlyList<UIPropertyCollection> collections, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry, string? key);
    void AddDefaultUICollectionItems(UIDocument document, IReadOnlyList<UIPropertyCollection> collections);
    void DrawUIDictionaryDefaultAdd(UIDocument document, IReadOnlyList<UIPropertyCollection> collections);
    void DrawRetainedUIDictionaryKey(UIDocument document, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollectionItem> items);
    void DrawUICollectionProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollection> collections);
    void DrawUICollectionItemProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollectionItem> items);
    void DrawUIAttachedProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key);
    void SelectUICollectionItemFactoryEntry(UIDocument document, IReadOnlyList<UIPropertyCollectionItem> items, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry);
    void SelectUIVirtualFactoryEntry(UIDocument document, IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter> presenters, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry);
    void SelectUIAttachedFactoryEntry(UIDocument document, IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry);
    void SelectUIFactoryEntry(UIDocument document, IReadOnlyList<Guid> ids, string path, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry);
    void RegisterPathDrop(UIDocument document, Type type, long revision, Func<bool> canEdit, Action<Stride.Core.IO.UPath> assign);
    void DrawExtensionProperties(UIDocument document);
    void DrawPropertyCommands(UIDocument document, IReadOnlyList<Stride.Core.Quantum.IMemberNode> nodes, bool canEdit);
    void DrawUnloadable(Stride.Core.Yaml.IUnloadable value);
    void ShowDocumentation(Stride.Core.Quantum.IGraphNode node, Type? rootType = null);
    void ShowDocumentation(Stride.Core.PropertyKey key);
    void ReportFailure(Exception error);
    void ReportValidationFailure(Exception error);
    void PublishAsset(UIAssetExport export, UIDocument source);
    void OpenSourceLibrary(Stride.Core.Assets.AssetId assetId, Guid elementId);
    bool CanOpenSourceLibrary(Stride.Core.Assets.AssetId assetId) => ResolveLibrary(assetId) != null;
    void OpenSourceLibraries(IReadOnlyList<Stride.Core.Assets.AssetId> assetIds)
    { foreach (var id in assetIds) OpenSourceLibrary(id, Guid.Empty); }
    void SelectSourceLibrary(Stride.Core.Assets.AssetId assetId);
    bool CanEditHierarchyOwner(UIDocument source) => false;
    UIHierarchyDragOwner? AcquireHierarchyDragOwner(UIDocument source) => null;
    UIHierarchyDragOwner? AcquireHierarchyCommandOwner(UIDocument source, bool writable) => null;
    bool DrawRenameTreeNode(string label, Hexa.NET.ImGui.ImGuiTreeNodeFlags flags, out bool virtualized)
        => DrawTreeNode(label, flags | Hexa.NET.ImGui.ImGuiTreeNodeFlags.Framed, true, out virtualized);
    UIHierarchyDragOwner? AcquireFactoryLibraryOwner(UIElementFactory factory) => null;
    bool IsCurrentFactoryDefinition(UIElementFactory factory) => false;
    bool DrawFactoryRow(string label, bool selected) => Hexa.NET.ImGui.ImGui.Selectable(label, selected);
    bool DrawFactoryCategoryGroup(string identity, string caption)
        => DrawTreeNode(caption + "###" + identity,
            Hexa.NET.ImGui.ImGuiTreeNodeFlags.DefaultOpen | Hexa.NET.ImGui.ImGuiTreeNodeFlags.SpanAvailWidth, false, out _);
    void ValidateHierarchyEdits() { }
    void DrawHierarchyDragText(string text) => Hexa.NET.ImGui.ImGui.TextUnformatted(text);
    bool DrawViewSettingsButton() => Hexa.NET.ImGui.ImGui.Button(EditorLocalization.Widget("Colors"));
    bool DrawZoomButton(string icon, string tooltip) => Hexa.NET.ImGui.ImGui.Button(tooltip);
    void DrawViewLabel(string text, bool bold = false) => Hexa.NET.ImGui.ImGui.TextUnformatted(text);
    bool BeginCameraCapture() => false;
    System.Numerics.Vector2? ConsumeCameraMotion() => null;
    void EndCameraCapture() { }
    (float Speed, float SceneUnit) CameraMotionSettings(UIDocument source) => (3, 1);
    UICameraView? LoadCameraView(UIDocument source) => null;
    void SaveCameraView(UIDocument source, UICameraView value) { }
    bool DrawUIPropertiesButton() => Hexa.NET.ImGui.ImGui.Button(EditorLocalization.Widget("UI properties", "Button"));
    bool DrawHierarchyExpansionButton(string icon, string tooltip) => Hexa.NET.ImGui.ImGui.Button(tooltip);
}
