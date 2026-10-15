using Stride.Assets.Materials;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.Editor.ImGui;

// Native counterpart of original MaterialAssetNodeUpdater, bound to the actual
// authored asset/context rather than a SessionViewModel or a duplicate asset.
internal sealed class EditorMaterialPropertyUpdater(Asset asset, PropertyKey<IEnumerable<string>> shaderNamesKey,
    Func<IEnumerable<string>> shaderNames) : NodePresenterUpdaterBase
{
    public override void UpdateNode(INodePresenter node)
    {
        if (asset is not MaterialAsset || node.Value is Stride.Core.Yaml.IUnloadable) return;
        if (node.Name == nameof(MaterialAsset.Attributes) && node.Value is MaterialAttributes && node.Parent != null)
        {
            node.IsVisible = false;
            foreach (var child in node.Children.ToArray()) child.ChangeParent(node.Parent);
        }
        if (node.Name == nameof(ComputeShaderClassColor.MixinReference) && node.Parent != null
            && typeof(IComputeNode).IsAssignableFrom(node.Parent.Type))
            node.AttachedProperties.Set(shaderNamesKey, EnumerateShaders());
        if (node.Value is not MaterialBlendLayer layer) return;
        node.DisplayName = !string.IsNullOrWhiteSpace(layer.Name) ? layer.Name : "Layer " + node.Index;
        if (node.TryGetChild(nameof(MaterialBlendLayer.Name)) is not { } name) return;
        name.IsVisible = false;
        if (node.Commands.All(command => command.Name != "RenameStringKey")) node.Commands.Add(new RenameLayerCommand());
        node.AddDependency(name, false);
    }
    private IEnumerable<string> EnumerateShaders()
    {
        foreach (var name in shaderNames()) yield return name;
    }
    private sealed class RenameLayerCommand : SyncNodePresenterCommandBase
    {
        public override string Name => "RenameStringKey";
        public override bool CanAttach(INodePresenter node) => node.Value is MaterialBlendLayer;
        public override bool CanExecute(IReadOnlyCollection<INodePresenter> nodes, object? parameter)
            => parameter is string && nodes.All(node => node.TryGetChild(nameof(MaterialBlendLayer.Name)) is { IsReadOnly: false });
        protected override void ExecuteSync(INodePresenter node, object? parameter, object? preExecuteResult)
        {
            if (parameter is not string name) throw new ArgumentException("The native layer rename requires a string parameter.");
            node[nameof(MaterialBlendLayer.Name)].UpdateValue(name);
        }
    }
}
