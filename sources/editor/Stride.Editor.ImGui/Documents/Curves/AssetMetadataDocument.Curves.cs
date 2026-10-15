using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal CurveBinding? BindCurve(IMemberNode member, string title)
    {
        var owner = historyScope.AuthoringState;
        if (owner == null || !CurveBinding.IsSupported(member.Retrieve())) return null;
        return CurveBinding.Create(new(owner, AuthoredAsset, AuthoringGraph, CanEditAssetSettingNode, CanEditAssetSettingItem, ApplyInheritedAssetMutation), member, title);
    }
}
