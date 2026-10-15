using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed class EditorCategoryNodeUpdater : NodePresenterUpdaterBase
{
    public override void UpdateNode(INodePresenter node)
    {
        if (node is not MemberNodePresenter member) return;
        if (member.MemberAttributes.OfType<System.ComponentModel.CategoryAttribute>().Any())
        {
            node.AttachedProperties.Set(CategoryData.Key, true);
            node.AttachedProperties.Set(DisplayData.AutoExpandRuleKey, ExpandRule.Always);
        }
        var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(member.MemberDescriptor.MemberInfo);
        if (node.Parent is not { } parent || string.IsNullOrEmpty(display?.Category)) return;
        var name = CategoryData.ComputeCategoryNodeName(display.Category);
        if (parent.Name == name) return;
        var category = parent.Children.FirstOrDefault(child => child.Name == name);
        if (category == null)
        {
            if (node.Factory is not EditorNodePresenterFactory factory) throw new InvalidOperationException("The native category has no editor presenter factory.");
            var metadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttributes<CategoryOrderAttribute>(member.MemberDescriptor.DeclaringType)
                .FirstOrDefault(order => order.Name == display.Category);
            category = factory.CreateVirtualNode(parent, name, typeof(string), default, _ => display.Category,
                order: metadata?.Order, configure: created =>
                {
                    created.DisplayName = display.Category;
                    created.AttachedProperties.Set(CategoryData.Key, true);
                    created.AttachedProperties.Set(DisplayData.AutoExpandRuleKey, metadata?.Expand ?? ExpandRule.Always);
                });
        }
        if (category is not VirtualNodePresenter || !NativePresenterCategories.IsCategory(category))
            throw new InvalidOperationException("The native property category conflicts with an existing node.");
        node.ChangeParent(category);
    }
}
