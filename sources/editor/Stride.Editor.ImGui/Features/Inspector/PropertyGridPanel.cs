using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using SVector3 = Stride.Core.Mathematics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace Stride.Editor.ImGui;

internal sealed partial class PropertyGridPanel(IPropertyGridHost host)
{
    internal bool Visible = true;
    internal Entity? SelectedEntity { get; set; }
    internal bool RenameActive { get; set; }
    internal bool AssetSelectionActive { get; set; }
    internal EntityHierarchyDocument? AssetContextScene { get; set; }
    internal long AssetContextSceneSelection { get; set; }
    internal string ComponentSearch = "";
    internal string SceneReferenceFilter = "";
    internal EntityComponent? PendingComponentRemoval { get; set; }
    internal Dictionary<ComponentField, string> ComponentErrors { get; } = [];
    internal ModelNodeNamePicker ModelNodeNames { get; } = new();
    internal PropertyStringEditor Strings { get; } = new();
    internal object? InspectorTransformLayout { get; private set; }

    internal ProceduralPresenterRenderState ProceduralRows { get; } = new();
    private readonly ConditionalWeakTable<Entity, NativeRotationEditorState> transformRotations = new();
    private static float UiScale(float value) => value * Gui.GetStyle().FontScaleDpi;
    private static NVector2 UiScale(float width, float height) => new(UiScale(width), UiScale(height));
    public void Draw(ref bool visible)
    {
        if (!visible) return;
        host.MigrateWindowTitle("Inspector", "Property grid###Inspector");
        using var window = ImGuiScopes.Window(EditorPanels.PropertyGrid.DisplayWindowName, ref visible);
        if (window.DrawContents)
        {
            if (Gui.IsWindowDocked()) host.RecordDocked();
            if (!host.TryDrawPackageProperties())
            {
                using var font = ImGuiScopes.Font(UiScale(12));
                using var padding = ImGuiScopes.Style(ImGuiStyleVar.FramePadding, UiScale(4, 4));
                using var spacing = ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, UiScale(4, 2));
                if (!DrawSceneRootProperties() && !host.TryDrawSelectedAssetProperties())
                {
                    if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) host.ActivateSceneContext();
                    using var disabled = ImGuiScopes.Disabled(host.ReadOnly);
                    Gui.BeginGroup();
                    try { DrawInspector(); }
                    finally { Gui.EndGroup(); }
                    host.DrawAssetComponentDrop();
                }
            }
        }
    }

    private void DrawInspector()
    {
        if (!ReferenceEquals(SelectedEntity, host.Document?.Selected))
        {
            SelectedEntity = host.Document?.Selected; Gui.SetScrollY(0);
        }
        if (host.CompositeSelectionCount > 1) { host.DrawAggregateProperties(); return; }
        var entity = host.Document?.Selected;
        if (entity == null) return;
        if (host.Document!.SelectedCount > 1) { host.DrawAggregateProperties(); return; }
        DrawInspectorEntityName(entity);
        host.DrawAddComponent(entity);
        var inspectorHeader = NVector4.Lerp(Gui.GetStyle().Colors[(int)ImGuiCol.WindowBg], Gui.GetStyle().Colors[(int)ImGuiCol.HeaderActive], 0.35f);
        Gui.PushStyleColor(ImGuiCol.Header, inspectorHeader);
        Gui.BeginDisabled(!host.Document.CanSave);
        var transformOpen = Gui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen);
        host.DrawTransformActions(entity.Transform);
        if (transformOpen)
        {
            var fields = host.Document.GetComponentFields(entity.Transform);
            if (fields.Any(field => (field.Node.Name is nameof(TransformComponent.Position) or nameof(TransformComponent.Rotation) or nameof(TransformComponent.Scale))
                && field.Node is Stride.Core.Assets.Quantum.IAssetMemberNode { BaseNode: not null }))
                host.DrawInheritedTransform(entity.Transform);
            else
            {
                var position = fields.First(field => field.Node.Name == nameof(TransformComponent.Position));
                var rotation = fields.First(field => field.Node.Name == nameof(TransformComponent.Rotation));
                var scale = fields.First(field => field.Node.Name == nameof(TransformComponent.Scale));
                EditVector("Position", position);
                var rotationEditor = transformRotations.GetValue(entity, _ => new());
                EditVector("Rotation", rotation, rotationEditor);
                EditVector("Scale", scale);
            }
        }
        Gui.EndDisabled();
        Gui.BeginDisabled(host.Document!.SelectedCount > 1);
        host.DrawComponents(entity);
        Gui.EndDisabled();
        Gui.PopStyleColor();
    }

    private void DrawInspectorEntityName(Entity entity)
    {
        if (!Gui.BeginTable("##EntityName", 2, ImGuiTableFlags.SizingStretchProp, new NVector2(PropertyGridRows.AvailableWidth(), 0))) return;
        Gui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, PropertyGridRows.LabelWidth(), 0u);
        Gui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f, 0u);
        Gui.TableNextRow(); Gui.TableSetColumnIndex(0);
        Gui.AlignTextToFramePadding(); Gui.TextUnformatted(EditorLocalization.Text("Name", "Menu"));
        Gui.TableSetColumnIndex(1); Gui.SetNextItemWidth(-1);
        var draft = RenameActive && host.RenameEntity == entity ? host.RenameDraft : entity.Name;
        var canRename = host.Document!.CanRenameEntity(entity);
        Gui.InputText("##InspectorName", ref draft, 4096, ImGuiInputTextFlags.AutoSelectAll
            | (canRename ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
        if (host.Controls.Enabled) host.Controls.Record("inspector/name");
        if (canRename && (Gui.IsItemActivated() || Gui.IsItemActive() && !RenameActive))
        {
            host.BeginRename(entity);
            RenameActive = host.RenameEntity == entity; 
        }
        if (RenameActive && host.RenameEntity == entity)
        {
            host.RenameDraft = draft;
            if (Gui.IsKeyPressed(ImGuiKey.Escape)) host.FinishRename(false);
            else if (Gui.IsItemDeactivated()) host.FinishRename(true);
        }
        host.DrawInheritedEntityResetMenu(entity);
        Gui.EndTable();
    }

    private void EditVector(string label, ComponentField field, NativeRotationEditorState? rotation = null)
    {
        var style = Gui.GetStyle();
        var available = PropertyGridRows.AvailableWidth();
        var labelWidth = PropertyGridRows.LabelWidth(available);
        var axisWidth = Gui.CalcTextSize("X").X + style.ItemInnerSpacing.X
            + Gui.CalcTextSize("0.00").X + style.FramePadding.X * 4 + style.CellPadding.X * 2;
        var inlineLabel = available >= labelWidth + style.CellPadding.X * 2 + axisWidth * 3;
        var axisColumns = inlineLabel ? 3 : Math.Clamp((int)(available / Math.Max(1, axisWidth)), 1, 3);
        if (host.Controls.Enabled && label == "Position")
        {
            var window = Hexa.NET.ImGui.ImGuiP.GetCurrentWindow();
            InspectorTransformLayout = new
            {
                label, available, rawAvailable = Gui.GetContentRegionAvail().X, labelWidth, axisWidth, inlineLabel, axisColumns,
                scrollbarSize = style.ScrollbarSize,
                scrollbarSizes = new { x = window.ScrollbarSizes.X, y = window.ScrollbarSizes.Y }, scrollbarX = window.ScrollbarX, scrollbarY = window.ScrollbarY,
                contentSize = new { x = window.ContentSize.X, y = window.ContentSize.Y }, windowSize = new { x = window.Size.X, y = window.Size.Y },
                innerRect = new { minimum = new { x = window.InnerRect.Min.X, y = window.InnerRect.Min.Y }, maximum = new { x = window.InnerRect.Max.X, y = window.InnerRect.Max.Y } },
                workRect = new { minimum = new { x = window.WorkRect.Min.X, y = window.WorkRect.Min.Y }, maximum = new { x = window.WorkRect.Max.X, y = window.WorkRect.Max.Y } },
                cursorScreen = new { x = Gui.GetCursorScreenPos().X, y = Gui.GetCursorScreenPos().Y },
                selectedEntityId = host.Document?.Selected?.Id.ToString(),
                boundary = "Actual Position transform layout before its native table, recorded only for enabled control capture."
            };
        }
        if (!inlineLabel) Gui.TextUnformatted(label);
        if (!Gui.BeginTable("##Transform/" + label, axisColumns + (inlineLabel ? 1 : 0), ImGuiTableFlags.SizingStretchProp, new NVector2(available, 0))) return;
        if (inlineLabel) Gui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, labelWidth, 0u);
        for (var axis = 0; axis < axisColumns; axis++) Gui.TableSetupColumn("XYZ"[axis].ToString(), ImGuiTableColumnFlags.WidthStretch, 1f, 0u);
        Gui.TableNextRow();
        if (inlineLabel) { Gui.TableSetColumnIndex(0); Gui.AlignTextToFramePadding(); Gui.TextUnformatted(label); }
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis > 0 && axis % axisColumns == 0) Gui.TableNextRow();
            Gui.TableSetColumnIndex(axis % axisColumns + (inlineLabel ? 1 : 0));
            var owner = host.Document!;
            var coordinate = axis;
            bool Editable() => ReferenceEquals(host.Document, owner) && !host.ReadOnly && owner.CanEditComponentValue(field)
                && (rotation != null || !field.IsAxisReadOnly(coordinate));
            using var axisDisabled = ImGuiScopes.Disabled(!Editable());
            PropertyGridRows.DrawAxisBadge(axis);
            Gui.SetNextItemWidth(-1);
            float ReadCoordinate(object? value) => rotation != null && value is SQuaternion quaternion
                ? rotation.Read(quaternion)[coordinate] : value is SVector3 vector ? vector[coordinate] : 0;
            string FormatCoordinate(object? value)
            {
                var places = rotation != null ? 3 : field.AxisNumericRanges.ElementAtOrDefault(coordinate)?.DecimalPlaces ?? 3;
                var format = places <= 0 ? "0" : "0." + new string('#', Math.Clamp(places, 0, 15));
                return ReadCoordinate(value).ToString(format, CultureInfo.InvariantCulture);
            }
            var control = "##" + label + "/" + axis;
            var feedback = Gui.GetID(control);
            try
            {
                Strings.Draw(field.Node, false, Editable, () => [field.Node.Retrieve()], text =>
                {
                    var snapshot = rotation?.Capture();
                    try
                    {
                        var number = PropertyConversionFeedback.Convert(feedback,
                            () => (float)NativeNumericText.Parse(text, typeof(float), rotation != null ? null : field.AxisNumericRanges.ElementAtOrDefault(coordinate)));
                        if (!float.IsFinite(number)) throw new ArgumentException("The coordinate must be finite.");
                        var current = field.Node.Retrieve();
                        object next;
                        if (rotation != null && current is SQuaternion quaternion) next = rotation.ReplaceAxis(quaternion, coordinate, number);
                        else if (current is SVector3 vector) { vector[coordinate] = number; next = vector; }
                        else throw new InvalidOperationException("The transform property's type changed.");
                        owner.UpdateComponent(field, next); owner.EndComponentEdit(); ComponentErrors.Remove(field);
                    }
                    catch { if (snapshot != null) rotation!.Restore(snapshot); throw; }
                }, () => host.AcquirePropertyDraftOwner(owner), text =>
                {
                    var range = rotation != null ? null : field.AxisNumericRanges.ElementAtOrDefault(coordinate);
                    return NativeNumericInput.StepText(typeof(float), ReadCoordinate(field.Node.Retrieve()), false, range, ref text) ? text : null;
                }, FormatCoordinate, controlLabel: control);
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportPropertyFailure(field, error); }
            if (host.Controls.Enabled) host.Controls.Record(label + "/" + axis);
            PropertyConversionFeedback.Draw(feedback);
            if (Gui.IsItemHovered()) Gui.SetTooltip(label + " " + "XYZ"[axis]);
        }
        Gui.EndTable();
    }

}
