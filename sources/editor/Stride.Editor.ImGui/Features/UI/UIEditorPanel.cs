using Hexa.NET.ImGui;
using Stride.UI.Editor.Game;
using Stride.Assets.Presentation.ViewModel;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.UI;
using Stride.Core;
using Stride.UI.Panels;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

/// <summary>Owns UI editor selection and interactions over canonical UI documents and actual Stride UI layout.</summary>
internal sealed partial class UIEditorPanel(IUIEditorHost host) : IDisposable
{
    private UIDocument? document;
    private UIEditorViewport? viewport;
    private readonly UIElementSelection selected = new();
    private readonly Dictionary<string, string> drafts = [];
    private Guid? displayedRoot;
    private Guid? moving;
    private ResizingDirection resizing;
    private long contentGeneration;
    private float hierarchyWidth;
    private UICameraView? restoreView;
    public UIDocument? Document => document;
    public IReadOnlyCollection<Guid> Selection => selected;
    private Guid? PrimarySelection => selected.Count == 0 ? null : selected.Last();
    private bool showUIAssetProperties;
    internal bool ShowingAssetProperties => showUIAssetProperties;
    public void SelectElement(UIDocument source, Guid id)
    {
        Activate(source);
        if (source.Find(id) != null) Select(id, false);
    }
    private Exception? viewportFailure;
    private long failedLayoutVersion = -1;
    private void Activate(UIDocument source)
    {
        selected.Added = () => { if (document != null) host.SelectionAdded(document); };
        var generation = host.ContentGeneration(source);
        if (!ReferenceEquals(document, source))
        {
            CancelUIElementDeletion();
            ClearUITreeKeyboard();
            CancelHierarchyExpansion();
            var sameAsset = document?.Asset.Id == source.Asset.Id;
            var previousView = sameAsset ? viewport?.CaptureCameraView() ?? restoreView : null;
            var previousSettings = sameAsset && document != null ? ViewSettings : null;
            StopCameraInput();
            CancelCanvasPointer();
            host.ValidateHierarchyEdits();
            document?.CommitEdits();
            SaveEditorSelection();
            SaveCameraView();
            document?.SelectExtensionElements([]);
            if (viewport != null) host.ReleaseViewport(viewport);
            viewport = null;
            document = source; selected.Clear(); drafts.Clear(); moving = null;
            showUIAssetProperties = true;
            if (previousSettings != null) { uiViewSettings.Remove(source); uiViewSettings.Add(source, previousSettings); }
            layoutPointerCaptured = layoutDragStarted = layoutRuntimeChanged = false;
            displayedRoot = source.Roots.FirstOrDefault()?.Id;
            RestoreEditorSelection(source);
            restoreView = previousView; viewportFailure = null; failedLayoutVersion = -1;
            contentGeneration = generation;
        }
        else if (contentGeneration != generation)
        {
            StopCameraInput();
            CancelCanvasPointer();
            StopLayoutDrag();
            if (viewport != null)
            {
                restoreView = viewport.CaptureCameraView();
                host.ReleaseViewport(viewport); viewport = null;
            }
            contentGeneration = generation; viewportFailure = null; failedLayoutVersion = -1;
        }
        if (viewport == null && (viewportFailure == null || failedLayoutVersion != source.LayoutVersion))
        {
            try
            {
                viewport = host.CreateViewport(source);
                restoreView ??= host.LoadCameraView(source);
                viewportFailure = null;
            }
            catch (Exception error) { FailViewport(source, error); }
        }
    }
    private void FailViewport(UIDocument source, Exception error)
    {
        StopCameraInput();
        CancelCanvasPointer();
        // Reporting rethrows device loss; only an isolated preview failure leaves CPU authoring active.
        host.ReportFailure(error);
        viewportFailure = error; failedLayoutVersion = source.LayoutVersion;
        StopLayoutDrag();
        if (viewport != null)
        {
            restoreView = viewport.CaptureCameraView();
            host.ReleaseViewport(viewport); viewport = null;
        }
    }

    public void Draw(UIDocument source)
    {
        try
        {
            Activate(source);
            BeginHierarchyFrame();
            if (selected.RemoveWhere(id => source.Find(id) == null) != 0) RefreshSelection();
            if (displayedRoot is { } root && source.Find(root) == null) displayedRoot = source.Roots.FirstOrDefault()?.Id;
            if (viewport != null)
            {
                try
                {
                    viewport.Visible = true; viewport.Synchronize(source, displayedRoot);
                    if (restoreView is { } view) { viewport.RestoreCameraView(view); restoreView = null; }
                }
                catch (Exception error) { FailViewport(source, error); }
            }
            DrawToolbar(source);
            var size = Gui.GetContentRegionAvail();
            var splitterWidth = 5 * Gui.GetStyle().FontScaleDpi;
            var minimumPaneWidth = Math.Min(size.X * .4f, Gui.GetFontSize() * 10);
            var maximumPaneWidth = Math.Max(minimumPaneWidth, size.X - minimumPaneWidth - splitterWidth - Gui.GetStyle().ItemSpacing.X * 2);
            var paneWidth = Math.Clamp(hierarchyWidth > 0 ? hierarchyWidth : Gui.GetFontSize() * 18,
                minimumPaneWidth, maximumPaneWidth);
            if (Gui.BeginChild("UIHierarchy", new NVector2(paneWidth, 0), ImGuiChildFlags.Borders))
            {
                // The column centre lies inside the separately scrollable
                // factory palette. Aim in the column's bottom padding so a
                // wheel reaches its own scrollbar instead of that palette.
                var hierarchyPosition = Gui.GetWindowPos(); var hierarchySize = Gui.GetWindowSize();
                host.RecordDiagnosticScrollArea("ui/hierarchy/body",
                    hierarchyPosition + new NVector2(hierarchySize.X * .5f, hierarchySize.Y - Gui.GetStyle().WindowPadding.Y * .5f - 2));
                DrawUIPropertiesCommand(source);
                DrawFactoryPalette(source);
                DrawHierarchyExpansionCommands(source);
                var treeSize = Gui.GetContentRegionAvail();
                treeSize.Y = Math.Max(treeSize.Y, Gui.GetFrameHeightWithSpacing() * 3);
                if (Gui.BeginChild("##UIElementTree", treeSize))
                {
                    BeginUITreeKeyboard(source);
                    PumpHierarchyExpansion(source);
                    foreach (var element in source.Roots.ToArray()) DrawElement(source, element);
                    CompleteUITreeKeyboard(source);
                    DrawHierarchyEmptyDrop(source);
                }
                ImGuiScopes.EndChild();
                host.RegisterCaptureItem("ui.hierarchy", "section", "Hierarchy");
            }
            ImGuiScopes.EndChild(); Gui.SameLine();
            Gui.InvisibleButton("##UIHierarchySplitter", new NVector2(splitterWidth, Math.Max(1, size.Y)));
            if (Gui.IsItemHovered() || Gui.IsItemActive()) Gui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            if (Gui.IsItemActive()) hierarchyWidth = Math.Clamp(paneWidth + Gui.GetIO().MouseDelta.X, minimumPaneWidth, maximumPaneWidth);
            Gui.SameLine();
            if (Gui.BeginChild("UICanvas", NVector2.Zero, ImGuiChildFlags.Borders))
            {
                try
                {
                    if (viewport != null) DrawCanvas(source);
                    else if (viewportFailure != null) host.DrawSelectableText("ui.render.error", viewportFailure.Message);
                }
                catch (Exception error) { FailViewport(source, error); }
            }
            ImGuiScopes.EndChild();
            DrawUIElementDeletion();
        }
        catch (Exception error) { host.ReportFailure(error); }
    }
    private void DrawToolbar(UIDocument source)
    {
        using (ImGuiScopes.Disabled(viewport == null))
        {
            if (host.DrawZoomButton("zoom-default", "Reset zoom")) viewport?.ResetZoom();
            Gui.SameLine();
            if (host.DrawZoomButton("zoom-out", "Zoom out")) viewport?.StepZoom(1);
            Gui.SameLine();
            if (host.DrawZoomButton("zoom-in", "Zoom in")) viewport?.StepZoom(-1);
        }
        Gui.SameLine(); DrawLayoutSnap();
        Gui.SameLine(); DrawViewColors();
        Gui.SameLine();
        using (ImGuiScopes.Disabled(selected.Count == 0))
        {
            if (Gui.Button(EditorLocalization.Widget("Copy", "Menu"))) CopySelection(source, false);
            Gui.SameLine();
            using (ImGuiScopes.Disabled(!source.CanSave)) if (Gui.Button(EditorLocalization.Widget("Cut", "Menu"))) CopySelection(source, true);
        }
        Gui.SameLine();
        var pasteParent = selected.Count == 1 ? selected.First() : (Guid?)null;
        var clipboard = Gui.GetClipboardTextS() ?? "";
        using (ImGuiScopes.Disabled(!source.CanPasteHierarchy(clipboard, pasteParent)))
            if (Gui.Button(EditorLocalization.Widget("Paste", "Menu"))) PasteSelection(source, clipboard, pasteParent);
        Gui.SameLine();
        using (ImGuiScopes.Disabled(!host.CanEditHierarchyOwner(source)))
        {
            if (Gui.BeginCombo("##AddUIElement", "Add element"))
            {
                foreach (var factory in host.Factories)
                {
                    var parent = selected.Count == 1 ? source.Find(selected.First()) : null;
                    using var disabled = ImGuiScopes.Disabled(!source.CanInsert(parent));
                    if (Gui.Selectable(factory.Name + "##" + factory.Library.Asset.Id + factory.ElementId))
                        QueueUIFactoryInsertion(source, factory);
                }
                Gui.EndCombo();
            }
            Gui.SameLine();
            using (ImGuiScopes.Disabled(selected.Count == 0))
            {
                if (Gui.Button(EditorLocalization.Widget("Delete", "Button"))) ExecuteUICommand("delete");
                Gui.SameLine();
                using (ImGuiScopes.Disabled(source.CommonRoots(selected).Any(id => source.Find(id)!.VisualParent is not Stride.UI.Panels.Panel)))
                    if (Gui.Button(EditorLocalization.Widget("Duplicate", "Menu"))) ExecuteUICommand("duplicate");
                Gui.SameLine();
                using (ImGuiScopes.Disabled(!AreUISiblings(source, selected.ToArray())))
                if (Gui.BeginCombo("##GroupUIElements", "Group into"))
                {
                    foreach (var factory in host.Factories.Where(factory => factory.IsPanelFactory))
                        if (Gui.Selectable(factory.Name + "##" + factory.ElementId)) QueueUIGrouping(source, selected.ToArray(), factory);
                    Gui.EndCombo();
                }
                Gui.SameLine();
                if (Gui.Button("Create library"))
                    QueueUICommand(source, selected.ToArray(), true, (asset, ids) => host.PublishAsset(asset.PrepareExport(ids, true), asset));
                Gui.SameLine();
                using (ImGuiScopes.Disabled(source.CommonRoots(selected).Count != 1))
                    if (Gui.Button("Create page"))
                        QueueUICommand(source, selected.ToArray(), true, (asset, ids) => host.PublishAsset(asset.PrepareExport(ids, false), asset));
            }
        }
    }
    private void CopySelection(UIDocument source, bool cut)
    {
        QueueUICommand(source, selected.ToArray(), cut, (asset, ids) =>
        {
            var text = asset.CopyHierarchy(ids);
            Gui.SetClipboardText(text);
            if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The UI hierarchy could not be written to the clipboard.");
            if (cut) { asset.Delete(ids); SelectUIResult([]); }
        });
    }
    private void PasteSelection(UIDocument source, string text, Guid? parent)
    {
        QueueUICommand(source, selected.ToArray(), true, (asset, _) =>
        {
            var inserted = asset.PasteHierarchy(text, parent, host.ResolveLibrary);
            SelectUIResult(inserted);
        });
    }
    private void RefreshSelection()
    {
        showUIAssetProperties = false;
        document?.SelectExtensionElements(selected);
        if (document != null && PrimarySelection is { } id)
        {
            var root = document.Find(id);
            while (root?.VisualParent != null) root = root.VisualParent;
            if (root != null) displayedRoot = root.Id;
        }
        host.SelectionChanged();
    }
    private void Select(Guid id, bool additive)
    {
        host.ValidateHierarchyEdits();
        if (!additive) selected.Clear();
        if (additive && selected.Contains(id)) selected.Remove(id); else selected.Add(id);
        RefreshSelection();
    }
    private void DrawCanvas(UIDocument source)
    {
        var origin = Gui.GetCursorScreenPos(); var size = NVector2.Max(new NVector2(1), Gui.GetContentRegionAvail());
        if (viewport!.RenderError is { } renderError) throw renderError;
        viewport.Resize((int)size.X, (int)size.Y);
        var draw = Gui.GetWindowDrawList(); draw.AddImage(viewport.TextureRef, origin, origin + size);
        Gui.InvisibleButton("##UIViewport", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        RecordCanvasParityItem();
        DrawFactoryCanvasTarget(source, origin, size);
        host.RegisterCaptureItem("ui.canvas", "viewport", "UI editor");
        var io = Gui.GetIO(); var mouse = io.MousePos - origin;
        var canvasHovered = Gui.IsItemHovered();
        DrawCameraInput(source, size, canvasHovered);
        if (factoryDrag == null && Gui.IsItemClicked(ImGuiMouseButton.Left))
            BeginCanvasPointer(source, mouse, size);
        DrawCanvasHierarchyDragSource();
        if (factoryDrag == null && moving is { } movingId && Gui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateLayoutDrag(source, movingId, mouse, size);
        CompleteCanvasPointer(source, mouse, canvasHovered);
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) StopLayoutDrag();
        DrawCanvasHighlight(origin, mouse, canvasHovered);
        DrawAdornmentCursor(mouse, size, canvasHovered);
        foreach (var id in viewport.OrderedAdornmentElements(selected).Reverse())
            if (viewport.TryElementRect(id, out var min, out var max))
            {
                var selectionStyle = ViewSettings.Selection;
                var selectionThickness = selectionStyle.Thickness * viewport.Zoom;
                var selectionOffset = new NVector2(selectionThickness / 2);
                draw.AddRectFilled(origin + min, origin + max, Gui.GetColorU32(selectionStyle.Color * .2f));
                draw.AddRect(origin + min - selectionOffset, origin + max + selectionOffset, Gui.GetColorU32(selectionStyle.Color),
                    0, ImDrawFlags.None, selectionThickness);
                if (source.CanSave)
                    foreach (var handle in Handles(min, max)) draw.AddRect(origin + handle.Point - new NVector2(SizingHandleHalfSize),
                        origin + handle.Point + new NVector2(SizingHandleHalfSize), Gui.GetColorU32(ViewSettings.Sizing.Color),
                        0, ImDrawFlags.None, ViewSettings.Sizing.Thickness * viewport.Zoom);
                foreach (var guide in viewport.MarginGuides(id))
                {
                    var color = Gui.GetColorU32(ViewSettings.Guideline.Color);
                    draw.AddLine(origin + guide.Start, origin + guide.End, color, ViewSettings.Guideline.Thickness * viewport.Zoom);
                    if (Math.Abs(guide.Value) > 1) draw.AddText(origin + (guide.Start + guide.End) / 2, color, guide.Value.ToString("0.###", System.Globalization.CultureInfo.CurrentUICulture));
                }
            }
        if (viewport.RenderError is { } error) host.DrawSelectableText("ui.render.error", error.Message);
        DrawCanvasContext(source, origin, size);
    }
    private void DrawPresentedUIField(Guid id, UIElementProperty field)
    {
            using var row = new PropertyRowScope(field.Name, field.Label);
            if (row.DrawContents)
            {
                host.ShowDocumentation(field.Node, document.Find(id)!.GetType());
                var canEdit = selected.All(elementId => document.Properties(elementId).Any(other => other.Name == field.Name && document.CanEdit(other)));
                var value = field.Node.Retrieve();
                {
                var mixed = selected.Any(elementId => !ContentAssetReference.AreEqual(document.Properties(elementId).Single(other => other.Name == field.Name).Node.Retrieve(), value));
                using var mixedScope = new MixedValueScope(mixed);
                if (value is Stride.Core.Yaml.IUnloadable unloaded)
                {
                    host.DrawUnloadable(unloaded);
                    if (field.SupportsObjectLifecycle) DrawObjectProperty(field, value, mixed);
                }
                else if (field.SupportsObjectLifecycle || selected.Any(elementId => document.ExtensionAbstractEntries(document.Properties(elementId).Single(other => other.Name == field.Name).Node) != null)) DrawObjectProperty(field, value, mixed);
                else if (Nullable.GetUnderlyingType(field.Type) != null)
                    DrawUIValue(field.Name, field.Label, field.Type, value,
                        selected.Select(elementId => document.Properties(elementId).Single(other => other.Name == field.Name).Node.Retrieve()).ToArray(),
                        canEdit, field.AllowsNull, change => document.TransformProperties(selected.ToArray(), field.Name, change),
                        numericRangeProvider: () => NativeNumericInput.Merge(Nullable.GetUnderlyingType(field.Type)!, selected.Select(elementId =>
                        { var other = document.Properties(elementId).Single(entry => entry.Name == field.Name); return document.ExtensionNumericRange(other.Node) ?? other.NumericRange; })),
                        axisNumericRangeProvider: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(field.Type), selected.Select(elementId =>
                            document.UIAxisNumericRanges(document.Properties(elementId).Single(entry => entry.Name == field.Name).Node).ElementAtOrDefault(axis))),
                        axisCanEditProvider: axis => selected.All(elementId => !document.IsUIAxisReadOnly(document.Properties(elementId).Single(entry => entry.Name == field.Name).Node, axis)),
                        valueTarget: MemberValueTarget(document, selected.ToArray(), field.Name));
                else if (field.Type == typeof(string))
                {
                    host.DrawStringProperty(document, selected.ToArray(), field.Name, mixed);
                }
                else if (value is bool boolean) { using var disabled = ImGuiScopes.Disabled(!canEdit); if (Gui.Checkbox("##Value", ref boolean)) CommitProperty(id, field, boolean); }
                else if (TryDrawUIRangedProperty(field, value, canEdit)) { }
                else if (AssetSettingNumericMetadata.Supports(field.Type))
                {
                    var source = document; var ids = selected.ToArray();
                    var fields = ids.Select(elementId => source.Properties(elementId).Single(entry => entry.Name == field.Name)).ToArray();
                    var target = MemberValueTarget(source, ids, field.Name);
                    host.DrawRetainedNumericProperty(source, target.Identity, field.Type, value, mixed,
                        target.CanEdit, target.Read, next => target.Transform(_ => next),
                        () => NativeNumericInput.Merge(field.Type, fields.Select(other => source.ExtensionNumericRange(other.Node) ?? other.NumericRange)),
                        () => Array.Empty<PropertyNumericRange?>());
                }
                else if (mixed && (value is float or int)) DrawMixedScalar(field, value, canEdit);
                else if (value is float scalar)
                {
                    var number = (double)scalar; var range = document.ExtensionNumericRange(field.Node) ?? field.NumericRange;
                    var changed = Gui.InputDouble("##Value", ref number, 0, 0,
                        range?.Format ?? "%.3f", canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly);
                    if (canEdit && NativeNumericInput.TryGesture(typeof(float), range, out var amount))
                    { number = Convert.ToDouble(NativeNumericInput.Step(typeof(float), scalar, range, amount)); changed = true; }
                    if (changed && canEdit) CommitProperty(id, field, number);
                }
                else if (value is int integer)
                {
                    var range = document.ExtensionNumericRange(field.Node) ?? field.NumericRange;
                    var changed = Gui.InputInt("##Value", ref integer, 0, 0, canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly);
                    if (canEdit) changed |= NativeNumericInput.StepValue(ref integer, range);
                    if (changed && canEdit) CommitProperty(id, field, integer);
                }
                else if (value is Vector2 or Vector3 or Vector4 or Thickness) DrawCoordinateProperties(field, value, canEdit);
                else if (field.Type.IsEnum) DrawEnumProperty(field, value, mixed, canEdit);
                else if (ContentAssetReference.Supports(field.Type))
                {
                    if (canEdit) host.DrawReference(field.Type, value, next => CommitProperty(id, field, next), mixed, field.AllowsNull);
                    else host.DrawSelectableText("##Value", mixed ? DifferentValues : ContentAssetReference.Read(value)?.Location ?? "");
                }
                else DrawUIValue(field.Name, field.Label, field.Type, value,
                    selected.Select(elementId => document.Properties(elementId).Single(other => other.Name == field.Name).Node.Retrieve()).ToArray(),
                    canEdit, field.AllowsNull, change => document.TransformProperties(selected.ToArray(), field.Name, change),
                    numericRangeProvider: () => NativeNumericInput.Merge(Nullable.GetUnderlyingType(field.Type) ?? field.Type, selected.Select(elementId =>
                    { var other = document.Properties(elementId).Single(entry => entry.Name == field.Name); return document.ExtensionNumericRange(other.Node) ?? other.NumericRange; })),
                    axisNumericRangeProvider: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(field.Type), selected.Select(elementId =>
                        document.UIAxisNumericRanges(document.Properties(elementId).Single(entry => entry.Name == field.Name).Node).ElementAtOrDefault(axis))),
                        axisCanEditProvider: axis => selected.All(elementId => !document.IsUIAxisReadOnly(document.Properties(elementId).Single(entry => entry.Name == field.Name).Node, axis)),
                        valueTarget: MemberValueTarget(document, selected.ToArray(), field.Name));
                }
                if (Gui.BeginPopupContextItem("Property actions"))
                {
                    try
                    {
                    var source = document; var ids = selected.ToArray(); var path = field.Name;
                    if (host.DrawMenuItem("Copy", "", false, ids.Length == 1))
                        QueueUICommand(source, ids, false, (asset, selection) => Gui.SetClipboardText(asset.CopyProperty(selection[0], path)));
                    var text = Gui.GetClipboardTextS() ?? "";
                    var canPaste = document.CanPasteProperties(ids, path, text);
                    if (host.DrawMenuItem("Paste", "", false, canPaste)) QueueUICommand(source, ids, true, (asset, selection) => asset.PasteProperties(selection, path, text, false));
                    if (host.DrawMenuItem("Replace", "", false, canPaste)) QueueUICommand(source, ids, true, (asset, selection) => asset.PasteProperties(selection, path, text, true));
                    host.DrawPropertyCommands(document, ids.Select(elementId => document.Properties(elementId).Single(other => other.Name == path).Node).ToArray(), canEdit);
                    if (field.Type.IsEnum && field.Type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        using var flagsDisabled = ImGuiScopes.Disabled(!canEdit);
                        if (FlagEnumPropertyControl.DrawBulkActions() is { } flagAction)
                            QueueUICommand(source, ids, true, (asset, selection) => asset.TransformProperties(selection, path,
                                value => FlagEnumSelection.Apply((Enum)(value ?? Enum.ToObject(field.Type, 0)), flagAction)));
                    }
                    using (ImGuiScopes.Disabled(!document.CanResetProperties(selected, field.Name)))
                        if (host.DrawMenuItem("Reset to base")) QueueUICommand(source, ids, true, (asset, selection) => asset.ResetProperties(selection, path));
                    }
                    finally { Gui.EndPopup(); }
                }
            }
    }

    private static IEnumerable<(NVector2 Point, ResizingDirection Direction)> Handles(NVector2 min, NVector2 max)
    {
        var center = (min + max) / 2;
        yield return (min, ResizingDirection.TopLeft);
        yield return (new NVector2(center.X, min.Y), ResizingDirection.Top);
        yield return (new NVector2(max.X, min.Y), ResizingDirection.TopRight);
        yield return (new NVector2(max.X, center.Y), ResizingDirection.Right);
        yield return (max, ResizingDirection.BottomRight);
        yield return (new NVector2(center.X, max.Y), ResizingDirection.Bottom);
        yield return (new NVector2(min.X, max.Y), ResizingDirection.BottomLeft);
        yield return (new NVector2(min.X, center.Y), ResizingDirection.Left);
    }
    public void DrawProperties()
    {
        if (document == null) return;
        document.SelectExtensionElements(selected);
        if (selected.Count == 0) return;
        var id = PrimarySelection!.Value; if (document.Find(id) == null) return;
        DrawPresentedUIProperties(document, id);
        DrawUICollectionProperties(document, selected.ToArray());
        DrawDependencyProperties(document, selected.ToArray());
        if (selected.Count == 1 && document.Find(id) is Grid)
        {
            Definitions(nameof(Grid.RowDefinitions));
            Definitions(nameof(Grid.ColumnDefinitions));
            Definitions(nameof(Grid.LayerDefinitions));
        }
        void Definitions(string name)
        {
            var source = document;
            var collection = source.GridDefinitionCollection(id, name);
            Gui.PushID(name);
            try
            {
                using (ImGuiScopes.Disabled(!host.CanEditHierarchyOwner(source) || !source.CanAddCollectionItem(collection)))
                    if (Gui.Button("Add " + Stride.Core.Presentation.Core.Utils.SplitCamelCase(name)))
                        QueueUICommand(source, [id], true, (asset, _) =>
                        {
                            var current = asset.GridDefinitionCollection(id, name);
                            if (!ReferenceEquals(current.Collection, collection.Collection)) throw new InvalidOperationException("The grid definition collection changed before adding.");
                            asset.AddCollectionItem(current, typeof(StripDefinition));
                        });
                foreach (var item in collection.Items)
                {
                    Gui.SameLine();
                    using var disabled = ImGuiScopes.Disabled(!host.CanEditHierarchyOwner(source) || !source.CanRemoveCollectionItem(item));
                    if (Gui.SmallButton("Remove " + item.Index))
                    {
                        var expected = item.Value;
                        QueueUICommand(source, [id], true, (asset, _) =>
                        {
                            var current = asset.GridDefinitionCollection(id, name);
                            if (!ReferenceEquals(current.Collection, item.Owner.Collection)) throw new InvalidOperationException("The grid definition collection changed before removal.");
                            if (!ReferenceEquals(item.Value, expected)) throw new InvalidOperationException("The grid definition changed before removal.");
                            asset.RemoveCollectionItem(item);
                        });
                        break;
                    }
                }
            }
            finally { Gui.PopID(); }
        }
    }
    private void CommitProperty(Guid id, UIElementProperty field, object? value)
    {
        if (selected.Count == 1) document!.SetProperty(id, field, value);
        else document!.SetProperties(selected.ToArray(), field.Name, value);
    }
    private void DrawDependencyProperties(UIDocument source, Guid[] ids)
    {
        var elements = ids.Select(source.Find).Where(element => element != null).ToArray();
        if (elements.Length != ids.Length || elements.Length == 0) return;
        var element = elements[0]!;
        if (elements.All(item => item!.VisualParent is Panel)) Integer(Panel.ZIndexPropertyKey, "Z index", null);
        if (elements.All(item => item!.VisualParent is GridBase))
        {
            Integer(GridBase.RowPropertyKey, "Row", 0); Integer(GridBase.ColumnPropertyKey, "Column", 0); Integer(GridBase.LayerPropertyKey, "Layer", 0);
            Integer(GridBase.RowSpanPropertyKey, "Row span", 1); Integer(GridBase.ColumnSpanPropertyKey, "Column span", 1); Integer(GridBase.LayerSpanPropertyKey, "Layer span", 1);
        }
        if (elements.All(item => item!.VisualParent is Canvas))
        {
            Vector(Canvas.PinOriginPropertyKey, "Pin origin");
            var absolute = element.DependencyProperties.Get(Canvas.UseAbsolutePositionPropertyKey);
            if (PropertyGridRows.BeginRow("Use absolute position"))
            {
                host.ShowDocumentation(Canvas.UseAbsolutePositionPropertyKey);
                {
                using var disabled = ImGuiScopes.Disabled(!ids.All(id => source.CanChangeDependencyProperty(id, Canvas.UseAbsolutePositionPropertyKey)));
                Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, Mixed(Canvas.UseAbsolutePositionPropertyKey));
                if (Gui.Checkbox("##UseAbsolutePosition", ref absolute)) source.SetDependencyProperties(ids, Canvas.UseAbsolutePositionPropertyKey, absolute);
                Gui.PopItemFlag();
                }
                Clipboard(Canvas.UseAbsolutePositionPropertyKey);
                Gui.EndTable();
            }
            Vector(Canvas.AbsolutePositionPropertyKey, "Absolute position");
            Vector(Canvas.RelativePositionPropertyKey, "Relative position");
            Vector(Canvas.RelativeSizePropertyKey, "Relative size");
        }
        var stockKeys = new HashSet<PropertyKey>
        {
            Panel.ZIndexPropertyKey, GridBase.RowPropertyKey, GridBase.ColumnPropertyKey, GridBase.LayerPropertyKey,
            GridBase.RowSpanPropertyKey, GridBase.ColumnSpanPropertyKey, GridBase.LayerSpanPropertyKey,
            Canvas.PinOriginPropertyKey, Canvas.UseAbsolutePositionPropertyKey, Canvas.AbsolutePositionPropertyKey,
            Canvas.RelativePositionPropertyKey, Canvas.RelativeSizePropertyKey,
        };
        foreach (var group in source.CommonAttachedProperties(ids).Where(property => !stockKeys.Contains(property.Key)).GroupBy(property => property.Category))
        {
            if (!string.IsNullOrEmpty(group.Key) && !Gui.CollapsingHeader(group.Key, ImGuiTreeNodeFlags.DefaultOpen)) continue;
            foreach (var property in group)
            {
                Gui.PushID(property.Key.OwnerType.FullName + "/" + property.Key.Name);
                try
                {
                    using var row = new PropertyRowScope("Attached", property.Label);
                    if (!row.DrawContents) continue;
                    host.ShowDocumentation(property.Key);
                    var values = ids.Select(id => source.ReadDependencyProperty(id, property.Key)).ToArray();
                    var entries = source.CommonUIAttachedEntries(ids, property.Key);
                    if (property.Key.PropertyType == typeof(string) && entries == null)
                        host.DrawRetainedStringProperty(source, source, values.Skip(1).Any(value => !Equals(value, values[0])),
                            () => ids.All(id => source.CanChangeDependencyProperty(id, property.Key)),
                            () => ids.Select(id => source.ReadDependencyProperty(id, property.Key))
                                .Concat(ids.SelectMany(id => new object?[] { source.Find(id), source.Find(id)?.VisualParent, property.Key })).ToArray(),
                            replacement => source.TransformDependencyPropertyValues(ids, property.Key, _ => replacement));
                    else DrawUIValue("attached/" + property.Key.OwnerType.FullName + "/" + property.Key.Name, property.Label, property.Key.PropertyType,
                        values[0], values, ids.All(id => source.CanChangeDependencyProperty(id, property.Key)),
                        !property.Key.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.Key.PropertyType) != null,
                        change => source.TransformDependencyPropertyValues(ids, property.Key, change), nativeEntries: entries,
                        selectEntry: entry => QueueUICommand(source, ids, true, (asset, selection) => host.SelectUIAttachedFactoryEntry(asset, selection, property.Key, entry)),
                        numericRangeProvider: () => NativeNumericInput.Merge(Nullable.GetUnderlyingType(property.Key.PropertyType) ?? property.Key.PropertyType, ids.Select(id => source.UIAttachedNumericRange(id, property.Key))),
                        axisNumericRangeProvider: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(property.Key.PropertyType), ids.Select(id => source.UIAttachedAxisNumericRanges(id, property.Key).ElementAtOrDefault(axis))),
                        axisCanEditProvider: axis => ids.All(id => !source.IsUIAttachedAxisReadOnly(id, property.Key, axis)),
                        valueTarget: AttachedValueTarget(source, ids, property.Key));
                    Clipboard(property.Key);
                }
                finally { Gui.PopID(); }
            }
        }
        bool Mixed<T>(PropertyKey<T> key) => elements.Skip(1).Any(item => !Equals(item!.DependencyProperties.Get(key), element.DependencyProperties.Get(key)));
        void Integer(PropertyKey<int> key, string label, int? minimum)
        {
            Gui.PushID(key.Name);
            if (PropertyGridRows.BeginRow(label))
            {
                host.ShowDocumentation(key);
                var value = element.DependencyProperties.Get(key);
                var canEdit = ids.All(id => source.CanChangeDependencyProperty(id, key));
                Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, Mixed(key));
                if (Gui.InputInt("##Value", ref value, 1, 100, canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly) && canEdit)
                    source.SetDependencyProperties(ids, key, minimum is { } lower ? Math.Max(lower, value) : value);
                Gui.PopItemFlag();
                Clipboard(key);
                Gui.EndTable();
            }
            Gui.PopID();
        }
        void Vector(PropertyKey<Vector3> key, string label)
        {
            Gui.PushID(key.Name);
            if (PropertyGridRows.BeginRow(label))
            {
                host.ShowDocumentation(key);
                var current = element.DependencyProperties.Get(key);
                var canEdit = ids.All(id => source.CanChangeDependencyProperty(id, key));
                if (Gui.BeginTable("##AttachedCoordinates", 3, ImGuiTableFlags.SizingStretchSame))
                {
                    try
                    {
                        for (var axis = 0; axis < 3; axis++)
                        {
                            Gui.TableNextColumn(); Gui.PushID(axis);
                            try
                            {
                                var coordinate = axis == 0 ? current.X : axis == 1 ? current.Y : current.Z;
                                var mixed = elements.Skip(1).Any(item => { var actual = item!.DependencyProperties.Get(key); return !(axis == 0 ? actual.X : axis == 1 ? actual.Y : actual.Z).Equals(coordinate); });
                                var draftKey = "attached/" + PropertyDraftKey(key.Name) + "/" + axis;
                                var text = drafts.GetValueOrDefault(draftKey, mixed ? "" : coordinate.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
                                Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine(); Gui.SetNextItemWidth(-float.Epsilon);
                                var submitted = Gui.InputTextWithHint("##Value", mixed ? DifferentValues : "", ref text, 64,
                                    ImGuiInputTextFlags.EnterReturnsTrue | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
                                var feedback = Gui.GetID("##Value");
                                if (canEdit && Gui.IsItemEdited()) drafts[draftKey] = text;
                                if (canEdit && (submitted || Gui.IsItemDeactivatedAfterEdit()) && drafts.ContainsKey(draftKey))
                                {
                                    try
                                    {
                                        var replacement = float.Parse(text, System.Globalization.CultureInfo.InvariantCulture); var index = axis;
                                        source.TransformDependencyProperties(ids, key, value => { if (index == 0) value.X = replacement; else if (index == 1) value.Y = replacement; else value.Z = replacement; return value; });
                                        drafts.Remove(draftKey); PropertyConversionFeedback.Validate(feedback);
                                    }
                                    catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportValidationFailure(error); }
                                }
                                PropertyConversionFeedback.Draw(feedback);
                                Clipboard(key);
                            }
                            finally { Gui.PopID(); }
                        }
                    }
                    finally { Gui.EndTable(); }
                }
                Gui.EndTable();
            }
            Gui.PopID();
        }
        void Clipboard(Stride.Core.PropertyKey key)
        {
            if (!Gui.BeginPopupContextItem("Attached property actions")) return;
            try
            {
                if (ContentAssetReference.Supports(key.PropertyType)) host.DrawReferenceFetchActions(ids.Select(id => source.ReadDependencyProperty(id, key)));
                if (key.PropertyType.IsEnum && key.PropertyType.IsDefined(typeof(FlagsAttribute), true))
                {
                    using var flagsDisabled = ImGuiScopes.Disabled(!ids.All(id => source.CanChangeDependencyProperty(id, key)));
                    if (FlagEnumPropertyControl.DrawBulkActions() is { } flagAction)
                        QueueUICommand(source, ids, true, (asset, selection) => asset.TransformDependencyPropertyValues(selection, key,
                            value => FlagEnumSelection.Apply((Enum)(value ?? Enum.ToObject(key.PropertyType, 0)), flagAction)));
                }
                if (host.DrawMenuItem("Copy", "", false, ids.Length == 1 && source.HasDependencyProperty(ids[0], key)))
                    QueueUICommand(source, ids, false, (asset, selection) => Gui.SetClipboardText(asset.CopyDependencyProperty(selection[0], key)));
                var text = Gui.GetClipboardTextS() ?? "";
                var canPaste = source.CanPasteDependencyProperty(ids, key, text);
                if (host.DrawMenuItem("Paste", "", false, canPaste)) QueueUICommand(source, ids, true, (asset, selection) => asset.PasteDependencyProperty(selection, key, text));
                if (host.DrawMenuItem("Replace", "", false, canPaste)) QueueUICommand(source, ids, true, (asset, selection) => asset.PasteDependencyProperty(selection, key, text, true));
                if (host.DrawMenuItem("Reset to base value", "", false, source.CanResetDependencyProperties(ids, key)))
                    QueueUICommand(source, ids, true, (asset, selection) => asset.ResetDependencyProperties(selection, key));
                host.DrawUIAttachedProviderCommands(source, ids, key);
            }
            finally { Gui.EndPopup(); }
        }
    }
    public void Render(GameTime time) => viewport?.Render(time);
    public bool ExecuteClipboardCommand(string command)
    {
        if (document == null) return false;
        switch (command)
        {
            case "copy" when selected.Count > 0: CopySelection(document, false); return true;
            case "cut" when selected.Count > 0 && document.CanSave: CopySelection(document, true); return true;
            case "paste":
                var text = Gui.GetClipboardTextS() ?? ""; var parent = selected.Count == 1 ? selected.First() : (Guid?)null;
                if (!document.CanPasteHierarchy(text, parent)) return false;
                PasteSelection(document, text, parent); return true;
            default: return false;
        }
    }
    public void ProcessRequests()
    {
        ProcessHierarchyDrop();
        ProcessFactoryDrop();
        ProcessUICommands();
    }
    public void ResetVisibility() { if (viewport != null) viewport.Visible = false; }
    public void Dispose() { ReleaseRecoveryReplacement(); ClearUITreeKeyboard(); CancelHierarchyExpansion(); CancelCanvasPointer(); StopCameraInput(); ClearUICommands(); ClearHierarchyTransfers(); document?.EndEdit(); SaveCameraView(); if (viewport != null) host.ReleaseViewport(viewport); viewport = null; if (document != null) uiViewSettings.Remove(document); document = null; }
}
