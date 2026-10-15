using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Gui = Hexa.NET.ImGui.ImGui;
using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool HasSpecializedSpriteFieldControl(SpriteSettingField field)
    {
        var type = field.Type;
        return type == typeof(Stride.Core.IO.UFile) && field.Node.Name == nameof(Stride.Assets.Sprite.SpriteInfo.Source)
            && field.Node.Parent.Retrieve() is Stride.Assets.Sprite.SpriteInfo || type == typeof(string)
        || AssetSettingNumericMetadata.Supports(type) || type == typeof(Stride.Core.Mathematics.Vector2)
        || type == typeof(Stride.Core.Mathematics.Vector4) || type == typeof(Stride.Core.Mathematics.Rectangle)
        || type == typeof(Stride.Core.Mathematics.Color) || type == typeof(bool) || type.IsEnum
        || sessionHistory?.Extensions?.HasPrimitiveEditor(type) == true;
    }
    private void DrawPresentedSpriteFrameSettings(AssetMetadataDocument owner)
        => DrawPresentedSpriteSettings(owner, owner.SpriteFramePresenterRoots);
    private void DrawPresentedSpriteSettings(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> roots)
    {
        var scope = GetAssetPresenterRenderScope(owner);
        // Every native selected-root virtual row belongs to this traversal, including closed subtrees.
        void Consume(INodePresenter node)
        {
            if (node is VirtualNodePresenter virtualNode) scope.Consumed.GetValue(virtualNode, _ => new object());
            foreach (var child in node.Children) Consume(child);
        }
        foreach (var root in roots) Consume(root);
        var groups = new List<IReadOnlyList<INodePresenter>>();
        void Collect(IReadOnlyCollection<INodePresenter> parents)
        {
            foreach (var group in NodePresenterCombination.CombineChildren(parents))
            { groups.Add(group); Collect(group); }
        }
        Collect(roots);
        var categories = new NativePresenterCategories(node =>
        {
            var group = groups.FirstOrDefault(candidate => ReferenceEquals(candidate[0], node));
            if (group == null || !Gui.BeginPopupContextItem("Category actions")) return;
            try { DrawSpritePresenterCommands(owner, group); } finally { Gui.EndPopup(); }
        }, node => groups.FirstOrDefault(candidate => ReferenceEquals(candidate[0], node))?.Any(node => node.IsVisible) ?? node.IsVisible);
        DrawChildren(roots);
        void DrawChildren(IReadOnlyCollection<INodePresenter> parents)
        {
            foreach (var group in NodePresenterCombination.CombineChildren(parents)
                .OrderBy(group => NativeRowPath(group[0]), NativeRowPathComparer.Instance))
            {
                if (!owner.IsCurrentSpritePresenterGroup(group) || !NodePresenterCombination.CanConstruct(group) || group.All(node => !node.IsVisible)) continue;
                if (NativePresenterCategories.IsCategory(group[0])) { DrawChildren(group); continue; }
                if (!categories.Before(group[0], includeSelf: false, includeMemberCategories: false)) continue;
                Gui.PushID("factory/" + NativePresenterCategories.Identity(group[0]));
                try { DrawSpritePresenterFactories(owner, group); } finally { Gui.PopID(); }
                var members = group.Select(node => node.GetNodeAccessor().Node).ToArray();
                if (group.All(node => node is MemberNodePresenter))
                {
                    if (owner.SelectedSpriteIds.Count == 0 && group.Count == 1
                        && owner.SpriteSettingsFields.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, members[0])) is { } sheetField
                        && (sheetField.Node.MemberDescriptor.DeclaringType == typeof(Stride.Assets.Sprite.SpriteSheetAsset)
                            || sheetField.Node.MemberDescriptor.DeclaringType == typeof(Stride.Assets.Sprite.PackingAttributes))
                        && HasSpecializedSpriteFieldControl(sheetField))
                    { DrawSpriteSetting(owner, sheetField); continue; }
                    if (group.Count == 1 && owner.SelectedSpriteFrame is { } frame
                        && frame.Fields.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, members[0])) is { } field)
                    {
                        if (HasSpecializedSpriteFieldControl(field)) { DrawSpriteSetting(owner, field); continue; }
                    }
                    var setting = owner.SpriteCommonSettings.FirstOrDefault(candidate => candidate.Fields.Length == group.Count
                        && candidate.Fields.Select(member => member.Node).Zip(members).All(pair => ReferenceEquals(pair.First, pair.Second)));
                    if (setting != null && HasSpecializedSpriteFieldControl(setting.First)) { DrawSpriteCommonSetting(owner, setting); continue; }
                }
                Gui.PushID(NativePresenterCategories.Identity(group[0]));
                try
                {
                    var type = Nullable.GetUnderlyingType(group[0].Type) ?? group[0].Type;
                    var scalar = type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                        || type == typeof(Stride.Core.Mathematics.Quaternion) || NativeNumericAxes.Names(type).Count != 0
                        || IsPathType(type) || IsTemporalType(type) || type == typeof(Guid) || type == typeof(Stride.Core.Mathematics.AngleSingle)
                        || IsPackageColorType(type) || ContentAssetReference.Supports(group[0].Type);
                    var custom = owner.SessionHistory?.Extensions?.HasPrimitiveEditor(group[0].Type) == true;
                    if (!scalar && !custom && !group.Any(node => owner.ReadSpritePresenter(node) is Stride.Core.Yaml.IUnloadable)
                        && group.Any(node => node.Children.Any(child => child.IsVisible)))
                    {
                        var expand = group[0].AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var expansionRule) ? expansionRule : Stride.Core.ExpandRule.Auto;
                        if (expand is Stride.Core.ExpandRule.Always or Stride.Core.ExpandRule.Once) Gui.SetNextItemOpen(true, ImGuiCond.Once);
                        else if (expand == Stride.Core.ExpandRule.Never) Gui.SetNextItemOpen(false, ImGuiCond.Once);
                        var inline = group.Select(node => node.AttachedProperties.TryGetValue(InlineData.InlineMemberKey, out var inlineMember) && inlineMember
                            ? node.TryGetChild(InlineData.InlinedProperty) : null).ToArray();
                        if (inline.All(node => node?.Type == typeof(bool)))
                        {
                            var targets = inline.Select(node => node!).ToArray(); var before = owner.CaptureSpritePresenterGroup(targets);
                            var flags = targets.Select(node => owner.ReadSpritePresenter(node) is true).ToArray(); var flag = flags[0];
                            using (ImGuiScopes.Disabled(IsAuthoringPipelineUpdating || targets.Any(node => !owner.CanEditSpritePresenter(node))))
                            {
                                Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, flags.Any(value => value != flag));
                                try
                                {
                                    if (DrawNativeCheckbox("##Inline", ref flag)) Execute(() =>
                                    {
                                        ValidateSpritePresenterAction(owner, targets, ref before);
                                        if (!owner.IsCurrentSpritePresenterGroup(targets) || !before.SequenceEqual(owner.CaptureSpritePresenterGroup(targets)))
                                            throw new InvalidOperationException("The inline sprite property changed.");
                                        ApplySpritePresenterGroup(owner, targets, (_, _) => flag);
                                    });
                                }
                                finally { Gui.PopItemFlag(); }
                            }
                            Gui.SameLine();
                        }
                        var open = Gui.TreeNodeEx(group[0].DisplayName);
                        if (Gui.BeginPopupContextItem("Property actions"))
                        { try { DrawSpritePresenterCommands(owner, group); } finally { Gui.EndPopup(); } }
                        if (open) { try { DrawChildren(group); } finally { Gui.TreePop(); } }
                    }
                    else DrawSpritePresenterValues(owner, group);
                }
                finally { Gui.PopID(); }
            }
        }
    }
}
