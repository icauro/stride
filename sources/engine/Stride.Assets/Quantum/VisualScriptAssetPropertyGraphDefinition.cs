using Stride.Assets.Scripts;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Assets.Quantum;

[AssetPropertyGraphDefinition(typeof(VisualScriptAsset))]
public sealed class VisualScriptAssetPropertyGraphDefinition : AssetPropertyGraphDefinition
{
    public override bool IsMemberTargetObjectReference(IMemberNode member, object? value)
        => member.Parent.Type == typeof(Link)
            && member.Name is nameof(Link.Source) or nameof(Link.Target)
            && value is Slot
            || base.IsMemberTargetObjectReference(member, value);
}
