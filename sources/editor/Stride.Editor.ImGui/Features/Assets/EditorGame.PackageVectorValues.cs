using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsPackageVectorType(Type type) => type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)
        || type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4);

    private void DrawPackageVectorValue(Package package, PackageSettingAccessor accessor, Type type, string label,
        bool editable, INodePresenter? presenter)
        => DrawPackageCoordinateValue(package, accessor, type, label, editable, presenter,
            NativeNumericAxes.Names(type).Count, "##PackageVectorAxes", true);

    private void DrawPackageCoordinateValue(Package package, PackageSettingAccessor accessor, Type type, string label,
        bool editable, INodePresenter? presenter, int columns, string tableId, bool axisBadges,
        Action<int>? coordinateLabel = null)
    {
        var names = NativeNumericAxes.Names(type);
        var tree = packageSettingTrees[(package, accessor.Key)];
        var declaredRoot = tree.ActiveProjection?.Type ?? accessor.Key.Type;
        var address = tree.ActiveProjection?.Address() ?? [];
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        if (!Gui.BeginTable(tableId, columns, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < names.Count; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var child = presenter?.TryGetChild(names[axis]);
                    var coordinateType = type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4)
                        || type == typeof(Rectangle) ? typeof(int) : typeof(float);
                    var feedback = Gui.GetID("##Value");
                    if (axisBadges) DrawInspectorAxisBadge(axis);
                    coordinateLabel?.Invoke(axis);
                    Gui.SetNextItemWidth(-1);
                    PropertyNumericRange? Range() => tree.Presentation?.NumericRange(child, coordinateType);
                    object? Read() => child?.Value;
                    string[] nativePath = child == null ? [] : PresenterPath(child);
                    var canEdit = editable && child != null && !PackagePresenterReadOnly(child);
                    DrawPackagePropertyString(package, accessor, "##Value", child, canEdit, Read, text =>
                    {
                        if (child == null) throw new InvalidOperationException("The native coordinate is unavailable.");
                        var converted = PropertyConversionFeedback.Convert(feedback, () =>
                        {
                            var normalized = NativeNumericText.Parse(text, coordinateType, Range());
                            // Stock floating vector, rectangle and matrix templates coerce changed components to three decimals.
                            return coordinateType == typeof(float) ? (object)MathF.Round((float)normalized!, 3) : normalized;
                        });
                        if (Equals(Read(), converted)) return;
                        QueueSettingMutation(package, accessor, tree, false, root =>
                        {
                            var presentation = new PackageSettingPresentation(root, tree.Nodes.NodeBuilder,
                                SessionAuthoringHistory?.Extensions?.AcquirePackagePresenters(package, accessor.Key, declaredRoot), declaredType: declaredRoot);
                            Exception? mutationFailure = null;
                            try
                            {
                                var target = ResolvePresenter(presentation, root, nativePath);
                                if (PackagePresenterReadOnly(target)) throw new InvalidOperationException("The native coordinate is read-only.");
                                var currentRange = presentation.NumericRange(target, coordinateType);
                                var next = currentRange?.Normalize(converted) ?? converted;
                                if (coordinateType == typeof(float)) next = MathF.Round((float)next!, 3);
                                target.UpdateValue(next!);
                                return presentation.ValueGraphRoot;
                            }
                            catch (Exception error) { mutationFailure = error; throw; }
                            finally
                            {
                                try { presentation.Dispose(); }
                                catch (Exception cleanupError)
                                {
                                    packageCommandCleanup.Add(presentation.Dispose);
                                    if (mutationFailure != null) throw new AggregateException(mutationFailure, cleanupError);
                                    throw;
                                }
                            }
                        }, address);
                    }, format: value => value is float number ? MathF.Round(number, 3).ToString(CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                        step: text => NativeNumericInput.StepText(coordinateType, Read(), false, Range(), ref text) ? text : null,
                        readValues: () => [Read(), presenter?.Value]);
                    PropertyConversionFeedback.Draw(feedback);
                    if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) break;
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }

}
