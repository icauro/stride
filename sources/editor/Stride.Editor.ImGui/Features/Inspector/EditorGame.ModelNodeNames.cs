using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Physics;
using Stride.SpriteStudio.Assets;
using Stride.SpriteStudio.Runtime;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsModelNodeName(ComponentField field) => field.Type == typeof(string) && field.Node.Name == nameof(ModelNodeLinkComponent.NodeName)
        && field.Path.Count == 1 && (field.Component is ModelNodeLinkComponent or SpriteStudioNodeLinkComponent or PhysicsComponent);
    private Asset? AuthoredProxyAsset(object? proxy)
    {
        if (proxy == null) return null;
        var reference = AttachedReferenceManager.GetAttachedReference(proxy);
        if (reference == null || workspace == null) return null;
        return authoring?.Find(reference.Id)?.Document is IAssetTagDocument retained ? retained.AuthoredAsset
            : workspace.AssetItems.FirstOrDefault(item => item.Id == reference.Id)?.Asset;
    }
    private IReadOnlyList<ModelNodeNameChoice> ModelNodeChoices(ComponentField field)
    {
        if (field.Component is SpriteStudioNodeLinkComponent sprite)
        {
            var sheet = sprite.Target?.Sheet ?? sprite.Entity?.Transform.Parent?.Entity?.Get<SpriteStudioComponent>()?.Sheet;
            return AuthoredProxyAsset(sheet) is SpriteStudioModelAsset asset ? asset.NodeNames.Select(name => new ModelNodeNameChoice(name, 0)).ToArray() : [];
        }
        var model = field.Component switch
        {
            ModelNodeLinkComponent link => link.Target?.Model ?? link.Entity?.Transform.Parent?.Entity?.Get<ModelComponent>()?.Model,
            PhysicsComponent physics => physics.Entity?.Get<ModelComponent>()?.Model,
            _ => null,
        };
        return AuthoredProxyAsset(model) is ModelAsset modelAsset && AuthoredProxyAsset(modelAsset.Skeleton) is SkeletonAsset skeleton
            ? skeleton.Nodes.Select(node => new ModelNodeNameChoice(node.Name, node.Depth)).ToArray() : [];
    }
    private void DrawModelNodeName(ComponentField field)
    {
        Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;
            try
            {
                var inherited = field.Node is Stride.Core.Assets.Quantum.IAssetMemberNode { BaseNode: not null };
                var owner = document!;
                DrawRetainedModelNodeName(owner.Asset.Id + "/" + field.Component.Id + "/" + field.Name,
                    [field], [owner], false,
                    () => ReferenceEquals(document, owner) && (inherited ? owner.CanOverrideInheritedComponent(field) : owner.CanEditComponentValue(field)),
                    () => ModelNodeChoices(field), value =>
                    {
                        if (inherited) owner.EditInheritedScalar(field, value); else owner.UpdateComponent(field, value);
                        owner.EndComponentEdit();
                    });
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
                DrawInheritedResetAction(field);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void DrawAggregateModelNodeName(CommonComponentProperty property)
    {
        Gui.PushID(property.Key);
        try
        {
            if (!BeginDocumentedComponentRow(property.First)) return;
            try
            {
                var fields = property.Fields.ToArray();
                var owners = fields.Select(field => aggregatePropertyOwners[field]).ToArray();
                DrawRetainedModelNodeName(property.Key, fields, owners, property.IsMixed, () => CanEditAggregateProperty(property), () =>
                {
                    var sets = property.Fields.Select(ModelNodeChoices).ToArray();
                    return sets.Length == 0 ? [] : sets[0].Where(node => sets.Skip(1).All(set => set.Any(other => other.Name == node.Name))).ToArray();
                }, value => ApplyAggregateProperty(property, _ => value));
                DrawAggregateReset(property);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
}
