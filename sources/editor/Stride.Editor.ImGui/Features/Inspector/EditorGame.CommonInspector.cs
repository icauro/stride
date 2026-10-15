using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string DifferentPropertyValues = "(Different values)";
    private readonly Dictionary<(string Property, int Axis), string> commonPropertyDrafts = [];
    private EntityHierarchyDocument? commonInspectorDocument;
    private long commonInspectorSelection = -1, commonInspectorStructure = -1;
    private IReadOnlyList<CommonPropertyPanel> commonPropertyPanels = [];
    private sealed record CommonPropertyCategory(ComponentPropertySection Presentation, IReadOnlyList<CommonComponentProperty> Properties);
    private sealed record CommonPropertyPanel(string Key, string Label, IReadOnlyList<CommonPropertyCategory> Categories, CommonMaterialGroup? Materials);

    private void DrawCommonInspector()
    {
        var owner = document!;
        if (commonInspectorDocument != owner || commonInspectorSelection != owner.SelectionVersion || commonInspectorStructure != owner.StructureVersion)
        {
            commonPropertyDrafts.Clear(); commonInheritedColorDrafts.Clear(); commonColorHexDrafts.Clear(); commonInspectorDocument = owner;
            commonInspectorSelection = owner.SelectionVersion; commonInspectorStructure = owner.StructureVersion;
            commonPropertyPanels = owner.GetCommonComponentProperties().GroupBy(property => property.Key[..property.Key.IndexOf('/')]).Select(group =>
            {
                var entries = group.ToArray(); var byField = entries.ToDictionary(property => property.First);
                var fields = entries.Select(property => property.First).ToArray();
                var categories = ComponentPropertyPresentation.GetSections(fields).Select(section =>
                    new CommonPropertyCategory(section, section.Fields.Select(member => byField[member]).ToArray())).ToArray();
                var model = entries.FirstOrDefault(property => EntityHierarchyDocument.IsModelReference(property.First));
                var materials = model == null ? null : new CommonMaterialGroup(model.Fields.Select(member => owner.GetComponentFields(member.Component).First(EntityHierarchyDocument.IsModelMaterials)).ToArray());
                return new CommonPropertyPanel(group.Key, entries[0].ComponentLabel, categories, materials);
            }).ToArray();
        }
        DrawInheritedCommonTransform();
        foreach (var panel in commonPropertyPanels)
        {
            Gui.PushID(panel.Key);
            if (Gui.CollapsingHeader(panel.Label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawPresentedCombinedComponentRows(panel.Categories.SelectMany(category => category.Properties).ToArray(), _ => owner, DrawCommonProperty);
                if (panel.Materials != null) DrawCommonModelMaterials(panel.Materials);
            }
            Gui.PopID();
        }
        DrawAdditionalCombinedVirtualPanels([owner], commonPropertyPanels.Select(panel => panel.Key));
        ApplyPendingCollectionAction();
    }

    private void DrawCommonProperty(CommonComponentProperty property)
    {
        var field = property.First; var value = property.Value; var mixed = property.IsMixed;
        if (property.Fields.Any(member => !member.IsVisible) && field.Node.Name != "Enabled") return;
        if (CanShowInheritedCommon(property)) { DrawInheritedCommonProperty(property); return; }
        Gui.PushID(property.Key); Gui.TextUnformatted(field.Label); DrawCommonPropertyActions(property); Gui.SetNextItemWidth(-1);
        if (property.First.IsColor) { DrawRetainedCombinedColor(property); Gui.PopID(); return; }
        if (UsesCombinedNumericDraft(property)) { DrawCombinedNumericDrafts(property); Gui.PopID(); return; }
        if (field.Type == typeof(string)) { DrawCommonString(property, false); DrawCommonPropertyActions(property); Gui.PopID(); return; }
        if (field.IsContentReference)
        {
            var owner = document!;
            DrawContentReferenceValue(field.Type, value, property.Fields.All(member => member.AllowsNullObject),
                reference => { owner.UpdateCommonProperty(property, reference); owner.EndCommonPropertyEdit(); }, mixed,
                canEdit: owner.CanEditCommonProperty(property));
            DrawCommonPropertyActions(property); Gui.PopID(); return;
        }
        Gui.BeginDisabled(!document!.CanEditCommonProperty(property));
        using var nullableRow = NativeNullableRow.Begin("##Nullable", field.IsNullable);
        if (field.IsNullable)
        {
            var present = property.Fields.Any(member => member.Node.Retrieve() != null);
            if (DrawNativeNullablePresence(present, out var create, property.Fields.All(member => member.AllowsNullObject)))
                CommitCommonProperty(property, create ? CreateNullableValue(field.Type) : null);
            nullableRow.ValueColumn();
            if (!present) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); nullableRow.Dispose(); Gui.EndDisabled(); DrawCommonPropertyActions(property); Gui.PopID(); return; }
        }
        if (field.Type == typeof(Stride.Core.IO.UFile) || field.Type == typeof(Stride.Core.IO.UDirectory)) DrawCommonPropertyPath(property, false);
        else if (field.IsNavigationGroup)
            DrawNavigationGroupValue(value, mixed, document.CanEditCommonProperty(property), updated => CommitCommonProperty(property, updated));
        else if (field.Type == typeof(Stride.Rendering.Compositing.SceneCameraSlotId))
        {
            DrawCameraSlotValue(value, updated => CommitCommonProperty(property, updated), mixed);
        }
        else if (field.IsContentReference)
        {
            DrawContentReferenceValue(field.Type, value, property.Fields.All(member => member.AllowsNullObject),
                updated => CommitCommonProperty(property, updated), mixed);
        }
        else if (field.Type == typeof(string)) DrawCommonString(property, false);
        else if (DrawNativeScalarTemplate(field.Node, field.Type, value, mixed, document!.CanEditCommonProperty(property), updated => CommitCommonProperty(property, updated))) { }
        else if (field.Type == typeof(bool))
        {
            var preview = mixed ? DifferentPropertyValues : value == null ? "None" : (bool)value ? "True" : "False";
            if (Gui.BeginCombo("##Value", preview))
            {
                if (Gui.Selectable("False", !mixed && Equals(value, false))) CommitCommonProperty(property, false);
                if (Gui.Selectable("True", !mixed && Equals(value, true))) CommitCommonProperty(property, true);
                Gui.EndCombo();
            }
        }
        else if (field.Type.IsEnum)
        {
            var preview = mixed ? DifferentPropertyValues : field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString() ?? "None";
            if (DrawComponentEnumArtwork(field, value, mixed, updated => CommitCommonProperty(property, updated)))
            {
                if (field.Type.IsDefined(typeof(FlagsAttribute), true) && Gui.BeginPopupContextItem("Flag actions"))
                {
                    if (DrawFlagBulkActions() is { } action)
                        Execute(() => { document!.UpdateCommonPropertyValues(property, member => ApplyFlagBulkAction((Enum)(member.Node.Retrieve() ?? Enum.ToObject(member.Type, 0)), action)); document.EndCommonPropertyEdit(); });
                    Gui.EndPopup();
                }
            }
            else if (Gui.BeginCombo("##Value", preview))
            {
                foreach (var option in field.EnumOptions)
                {
                    Gui.PushID(option.Name);
                    if (Gui.Selectable(option.Label, !mixed && Equals(value, option.Value))) CommitCommonProperty(property, option.Value);
                    Gui.PopID();
                }
                Gui.EndCombo();
            }
        }
        else if (field.IsSceneReference)
        {
            var referenceOpen = Gui.BeginCombo("##Reference", mixed ? DifferentPropertyValues : SceneReferenceLabel(value));
            if (!referenceOpen) DrawSceneReferenceDrop(field.Type, document!.CanEditCommonProperty(property), candidate => CommitCommonProperty(property, candidate));
            if (referenceOpen)
            {
                void Candidate(object? candidate, string key)
                {
                    Gui.PushID(key);
                    var label = SceneReferenceLabel(candidate); var origin = Gui.GetCursorScreenPos();
                    var clicked = Gui.Selectable("##Candidate", !mixed && ReferenceEquals(value, candidate));
                    Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), label);
                    if (clicked) CommitCommonProperty(property, candidate);
                    Gui.PopID();
                }
                if (property.Fields.All(field => field.AllowsNullObject)) Candidate(null, "None");
                foreach (var part in document.Asset.Hierarchy.Parts.Values)
                {
                    if (SceneObjectReference.AcceptsEntity(field.Type, part.Entity)) Candidate(part.Entity, part.Entity.Id.ToString());
                    else foreach (var component in part.Entity.Components.Where(field.Type.IsInstanceOfType)) Candidate(component, component.Id.ToString());
                }
                Gui.EndCombo();
            }
        }
        else if (field.IsColor)
            DrawInheritedCommonColor(property, create => Execute(() => { document!.UpdateCommonPropertyValues(property, create); document.EndCommonPropertyEdit(); }), () => { });
        else if (field.Type == typeof(Quaternion))
        {
            var rotationView = Gui.GetID("##Rotation");
            for (var axis = 0; axis < 3; axis++)
            {
                var index = axis; Gui.PushID(axis); Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine(); Gui.SetNextItemWidth(-1);
                var coordinate = RotationCoordinate(field, rotationView, axis);
                var axisMixed = property.Fields.Skip(1).Any(other => RotationCoordinate(other, rotationView, axis) != coordinate);
                DrawCommonTextValue(property, axis, coordinate, axisMixed, text => document!.UpdateCommonPropertyValues(property,
                    other => ReplaceRotationCoordinate(other, rotationView, index, float.Parse(text, CultureInfo.InvariantCulture))));
                Gui.PopID();
            }
        }
        else if (CommonValueDimensions(field.Type) is var dimensions && dimensions != 0)
        {
            var matrixTable = UsesExtendedMathTable(field.Type) && Gui.BeginTable("##Matrix", 4, ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!UsesExtendedMathTable(field.Type) || matrixTable); axis++)
            {
                Gui.PushID(axis);
                DrawExtendedMathCoordinateLabel(field.Type, axis, matrixTable);
                Gui.SetNextItemWidth(-1);
                var coordinate = CommonCoordinate(value, axis);
                var axisMixed = property.Fields.Skip(1).Any(other => !Equals(CommonCoordinate(other.Node.Retrieve(), axis), coordinate));
                var integral = field.IsIntegerVector || field.Type == typeof(Rectangle);
                DrawCommonTextValue(property, axis, coordinate, axisMixed, text =>
                {
                    object number = field.Type == typeof(UInt4) ? uint.Parse(text, CultureInfo.InvariantCulture) : integral ? (object)int.Parse(text, CultureInfo.InvariantCulture) : float.Parse(text, CultureInfo.InvariantCulture);
                    var owner = document!;
                    owner.UpdateCommonPropertyValues(property, other => WithCommonCoordinate(other.Node.Retrieve(), other.Type, axis, number));
                });
                Gui.PopID();
            }
            if (matrixTable) Gui.EndTable();
        }
        else if (field.SupportsEditing)
        {
            DrawCommonTextValue(property, -1, value, mixed, text =>
            {
                object parsed = field.Type == typeof(string) ? text : field.Type == typeof(decimal)
                    ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture) : Convert.ChangeType(text, field.Type, CultureInfo.InvariantCulture);
                document!.UpdateCommonProperty(property, parsed);
            });
        }
        else Gui.TextUnformatted(mixed ? DifferentPropertyValues : value?.ToString() ?? "None");
        nullableRow.Dispose(); Gui.EndDisabled(); DrawCommonPropertyActions(property); Gui.PopID();
    }

    private void DrawCommonTextValue(CommonComponentProperty property, int axis, object? value, bool mixed, Action<string> apply)
    {
        using var axisDisabled = ImGuiScopes.Disabled(axis >= 0 && property.Fields.Any(field => field.IsAxisReadOnly(axis)));
        var key = (property.Key, axis);
        var text = commonPropertyDrafts.TryGetValue(key, out var draft) ? draft : mixed ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var editable = document!.CanEditCommonProperty(property);
        var readonlyString = property.First.Type == typeof(string) && !editable;
        if (readonlyString) { commonPropertyDrafts.Remove(key); text = mixed ? "" : (string?)value ?? ""; }
        var sliderCommitted = DrawRangedPropertyText(property.Fields, axis, value, mixed, ref text);
        var changed = sliderCommitted;
        changed |= NativeStringInput.DrawWithHint("##Value", mixed ? DifferentPropertyValues : value == null ? "None" : "", ref text,
            readonlyString ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
        changed |= StepPropertyText(property.Fields, axis, value, mixed, ref text);
        var active = Gui.IsItemActive(); var deactivated = Gui.IsItemDeactivated();
        if (editable && active) commonPropertyDrafts[key] = text; else commonPropertyDrafts.Remove(key);
        var feedback = Gui.GetID("##Value");
        if (editable && changed) Execute(() => PropertyConversionFeedback.Run(feedback, () => apply(text)));
        PropertyConversionFeedback.Draw(feedback);
        if (deactivated || sliderCommitted) document!.EndCommonPropertyEdit();
    }

    private void CommitCommonProperty(CommonComponentProperty property, object? value)
        => Execute(() => { document!.UpdateCommonProperty(property, value); document.EndCommonPropertyEdit(); });

    private static int CommonValueDimensions(Type type) => type == typeof(Vector2) || type == typeof(Int2) ? 2
        : type == typeof(Vector3) || type == typeof(Int3) || type == typeof(Color3) ? 3
        : type == typeof(Vector4) || type == typeof(Int4) || type == typeof(UInt4) || type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Color4) || type == typeof(Rectangle) || type == typeof(RectangleF) ? 4 : type == typeof(Matrix) ? 16 : 0;

    private static object? CommonCoordinate(object? value, int axis) => value switch
    {
        UInt4 v => v[(uint)axis], Matrix v => v[axis],
        Rectangle v => axis == 0 ? v.X : axis == 1 ? v.Y : axis == 2 ? v.Width : v.Height,
        RectangleF v => axis == 0 ? v.X : axis == 1 ? v.Y : axis == 2 ? v.Width : v.Height,
        Vector2 v => axis == 0 ? v.X : v.Y, Vector3 v => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z,
        Vector4 v => axis == 0 ? v.X : axis == 1 ? v.Y : axis == 2 ? v.Z : v.W,
        Quaternion v => axis == 0 ? v.X : axis == 1 ? v.Y : axis == 2 ? v.Z : v.W,
        Color v => axis == 0 ? v.R : axis == 1 ? v.G : axis == 2 ? v.B : v.A,
        Color3 v => axis == 0 ? v.R : axis == 1 ? v.G : v.B, Color4 v => axis == 0 ? v.R : axis == 1 ? v.G : axis == 2 ? v.B : v.A,
        Int2 v => axis == 0 ? (object)v.X : v.Y, Int3 v => axis == 0 ? (object)v.X : axis == 1 ? v.Y : v.Z,
        Int4 v => axis == 0 ? (object)v.X : axis == 1 ? v.Y : axis == 2 ? v.Z : v.W,
        _ => null,
    };

    private static object WithCommonCoordinate(object? value, Type type, int axis, object coordinate)
    {
        if (type == typeof(Rectangle))
        {
            var rectangle = value is Rectangle authored ? authored : throw new InvalidOperationException("The property no longer supplies a coordinate target."); var number = (int)coordinate;
            if (axis == 0) rectangle.X = number; else if (axis == 1) rectangle.Y = number; else if (axis == 2) rectangle.Width = number; else rectangle.Height = number;
            return rectangle;
        }
        if (type == typeof(RectangleF))
        {
            var rectangle = value is RectangleF authored ? authored : throw new InvalidOperationException("The property no longer supplies a coordinate target."); var number = MathF.Round((float)coordinate, 3);
            if (axis == 0) rectangle.X = number; else if (axis == 1) rectangle.Y = number; else if (axis == 2) rectangle.Width = number; else rectangle.Height = number;
            return rectangle;
        }
        if (type == typeof(Color))
        {
            var color = value as Color? ?? default;
            var channel = Convert.ToByte(Math.Clamp(Math.Round(Convert.ToDouble(coordinate), 0), 0, 255));
            if (axis == 0) color.R = channel; else if (axis == 1) color.G = channel; else if (axis == 2) color.B = channel; else color.A = channel;
            return color;
        }
        if (type == typeof(UInt4)) { var vector = value as UInt4? ?? default; vector[(uint)axis] = (uint)coordinate; return vector; }
        if (type == typeof(Matrix)) { var matrix = value as Matrix? ?? default; matrix[axis] = (float)coordinate; return matrix; }
        if (type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4))
        {
            var numbers = Enumerable.Range(0, CommonValueDimensions(type)).Select(index => Convert.ToInt32(CommonCoordinate(value, index) ?? 0)).ToArray();
            numbers[axis] = (int)coordinate;
            return type == typeof(Int2) ? (object)new Int2(numbers[0], numbers[1]) : type == typeof(Int3)
                ? new Int3(numbers[0], numbers[1], numbers[2]) : new Int4(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
        var values = Enumerable.Range(0, CommonValueDimensions(type)).Select(index => Convert.ToSingle(CommonCoordinate(value, index) ?? (type == typeof(Quaternion) && index == 3 ? 1f : 0f))).ToArray();
        values[axis] = (float)coordinate;
        return type == typeof(Vector2) ? (object)new Vector2(values[0], values[1]) : type == typeof(Vector3) ? new Vector3(values[0], values[1], values[2])
            : type == typeof(Vector4) ? new Vector4(values[0], values[1], values[2], values[3]) : type == typeof(Quaternion) ? new Quaternion(values[0], values[1], values[2], values[3])
            : type == typeof(Color3) ? new Color3(values[0], values[1], values[2]) : new Color4(values[0], values[1], values[2], values[3]);
    }
}
