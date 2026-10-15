using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorPropertyDocumentation PropertyDocumentation => Services.GetPropertyDocumentation(ResolveDocumentationAssembly);
    internal AssetUserDocumentation InspectorUserDocumentation => PropertyDocumentation.Store;
    private string? ResolveDocumentationAssembly(Assembly assembly)
        => workspace?.Session.Packages.SelectMany(package => package.LoadedAssemblies)
            .FirstOrDefault(item => ReferenceEquals(item.Assembly, assembly))?.Path;
    private bool BeginDocumentedPropertyRow(string label, IGraphNode node, Type? rootType = null, bool drawLabel = true)
    {
        if (!BeginInspectorPropertyRow(label, drawLabel)) return false;
        if (drawLabel) PropertyDocumentation.Show(node, rootType);
        return true;
    }
    private bool BeginDocumentedComponentRow(ComponentField field, string? label = null)
        => BeginDocumentedPropertyRow(label ?? field.Label, field.Node, field.Component.GetType());
    private void DrawDocumentedComponentLabel(ComponentField field, string? label = null)
    {
        DrawNativeLabel(label ?? field.Label);
        PropertyDocumentation.Show(field.Node, field.Component.GetType());
    }
    private void ShowCompositorDocumentation(CompositorProperty property)
    {
        if (property.Accessor.IsItem && property.Accessor.Index.Value is Stride.Core.PropertyKey key) PropertyDocumentation.Show(key);
        else PropertyDocumentation.Show(property.Accessor.Node);
    }
    private void ShowCompositorDocumentation(CompositorAggregateProperty property)
        => ShowCompositorDocumentation(property.First);
}
