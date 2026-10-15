using Microsoft.CodeAnalysis;
using Stride.Assets.Scripts;
using Stride.Core.Quantum;
using ScriptAccessibility = Stride.Assets.Scripts.Accessibility;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    internal Property AddProperty()
    {
        var property = new Property("bool", "Member" + Asset.Properties.Count);
        var node = CollectionNode(Asset, nameof(VisualScriptAsset.Properties));
        MutateCollection(node, () => node.Add(property), "Add property"); return property;
    }
    internal void RemoveProperties(IReadOnlyCollection<Guid> ids)
    {
        var values = Asset.Properties.Where(property => ids.Contains(property.Id)).ToArray();
        if (values.Length == 0) return;
        var node = CollectionNode(Asset, nameof(VisualScriptAsset.Properties));
        MutateCollection(node, () =>
        {
            foreach (var property in values)
            {
                var index = new NodeIndex(Asset.Properties.IndexOf(property));
                if (!CanEditAssetSettingItem(node, index)) throw new InvalidOperationException("The property is inherited and sealed.");
                node.Remove(property, index);
            }
        }, "Delete propertie(s)");
    }
    internal void MoveProperty(Property property, int destination)
    {
        var source = Asset.Properties.IndexOf(property);
        if (source < 0 || destination < 0 || destination >= Asset.Properties.Count || source == destination) return;
        var node = CollectionNode(Asset, nameof(VisualScriptAsset.Properties)); var index = new NodeIndex(source);
        if (!CanEditAssetSettingItem(node, index)) throw new InvalidOperationException("The property is inherited and sealed.");
        var item = node.IndexToId(index);
        MutateCollection(node, () => { node.Remove(property, index); node.Restore(property, new NodeIndex(destination), item); }, "Move property");
    }
    internal Method AddMethod(IMethodSymbol? symbol = null)
    {
        var method = new Method(symbol?.Name ?? "Method" + Asset.Methods.Count);
        if (symbol != null)
        {
            method.ReturnType = symbol.ReturnType.ToDisplayString();
            method.IsStatic = symbol.IsStatic;
            method.Accessibility = symbol.DeclaredAccessibility switch
            {
                Microsoft.CodeAnalysis.Accessibility.Public => ScriptAccessibility.Public,
                Microsoft.CodeAnalysis.Accessibility.Internal => ScriptAccessibility.Internal,
                Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => ScriptAccessibility.ProtectedOrInternal,
                _ => ScriptAccessibility.Protected
            };
            method.VirtualModifier = symbol.ContainingType.TypeKind == TypeKind.Interface ? VirtualModifier.Virtual : VirtualModifier.Override;
            foreach (var parameter in symbol.Parameters.Where(parameter => !parameter.IsThis))
                method.Parameters.Add(new Parameter(parameter.Type.ToDisplayString(), string.IsNullOrEmpty(parameter.Name) ? "param" + (parameter.Ordinal + 1) : parameter.Name)
                { RefKind = parameter.RefKind switch { RefKind.Ref => ParameterRefKind.Ref, RefKind.Out => ParameterRefKind.Out, _ => ParameterRefKind.None } });
        }
        var node = CollectionNode(Asset, nameof(VisualScriptAsset.Methods));
        MutateCollection(node, () => node.Add(method), "Add method"); SelectedMethodId = method.Id; return method;
    }
    internal void RemoveMethod(Method method)
    {
        RequireMethod(method); var node = CollectionNode(Asset, nameof(VisualScriptAsset.Methods));
        var index = new NodeIndex(Asset.Methods.IndexOf(method));
        if (!CanEditAssetSettingItem(node, index)) throw new InvalidOperationException("The method is inherited and sealed.");
        MutateCollection(node, () => node.Remove(method, index), "Delete method");
        if (SelectedMethodId == method.Id) SelectedMethodId = Asset.Methods.FirstOrDefault()?.Id;
    }
    internal Parameter AddParameter(Method method)
    {
        RequireMethod(method); var parameter = new Parameter("bool", "variable" + method.Parameters.Count);
        var node = CollectionNode(method, nameof(Method.Parameters));
        MutateCollection(node, () => node.Add(parameter), "Add parameter"); return parameter;
    }
    internal void RemoveParameters(Method method, IReadOnlyCollection<Guid> ids)
    {
        RequireMethod(method); var values = method.Parameters.Where(parameter => ids.Contains(parameter.Id)).ToArray();
        if (values.Length == 0) return;
        var node = CollectionNode(method, nameof(Method.Parameters));
        MutateCollection(node, () =>
        {
            foreach (var parameter in values)
            {
                var index = new NodeIndex(method.Parameters.IndexOf(parameter));
                if (!CanEditAssetSettingItem(node, index)) throw new InvalidOperationException("The parameter is inherited and sealed.");
                node.Remove(parameter, index);
            }
        }, "Delete variable(s)");
    }
}
