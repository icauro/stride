using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed class EditorMathematicsNodeUpdater : NodePresenterUpdaterBase
{
    private static readonly Type[] Types = [typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Int2), typeof(Int3), typeof(Int4), typeof(Matrix), typeof(Rectangle), typeof(RectangleF)];
    public override void UpdateNode(INodePresenter node)
    {
        if (!Types.Contains(node.Type) || node.Factory is not EditorNodePresenterFactory factory) return;
        foreach (var field in node.Type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            // Some bounded graphs already expose their real struct fields. Reuse
            // those native nodes instead of creating a duplicate field identity.
            if (node.TryGetChild(field.Name) is { } existing) { existing.IsVisible = false; continue; }
            var accessor = node.GetNodeAccessor();
            var writable = accessor.Node is IMemberNode || accessor.Node is IObjectNode && !accessor.Index.IsEmpty;
            factory.CreateVirtualNode(node, field.Name, field.FieldType, accessor,
                _ => field.GetValue(node.Value)!, writable ? (_, component) =>
                {
                    var value = RuntimeHelpers.GetObjectValue(node.Value);
                    field.SetValue(value, component); node.UpdateValue(value);
                } : null, field.GetCustomAttribute<DataMemberAttribute>()?.Order,
                component =>
                {
                    component.IsVisible = false;
                    new NumericValueNodeUpdater().UpdateNode(component, field);
                });
        }
    }
}
