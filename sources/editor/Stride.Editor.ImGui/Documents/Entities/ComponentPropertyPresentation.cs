using System.Runtime.CompilerServices;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record ComponentPropertySection(string? Category, int? Order, ExpandRule Expand, IReadOnlyList<ComponentField> Fields);

internal static class ComponentPropertyPresentation
{
    private static readonly ConditionalWeakTable<object, ComponentPropertySection[]> Sections = new();
    internal static IComparer<ComponentField> FieldComparer { get; } = Comparer<ComponentField>.Create(CompareFields);

    private static int CompareFields(ComponentField left, ComponentField right)
    {
        var order = (left.DisplayOrder ?? 0).CompareTo(right.DisplayOrder ?? 0);
        if (order != 0) return order;
        var a = left.Node.MemberDescriptor.MemberInfo; var b = right.Node.MemberDescriptor.MemberInfo;
        if (a.DeclaringType == b.DeclaringType) return a.MetadataToken.CompareTo(b.MetadataToken);
        if (a.DeclaringType != null && b.DeclaringType != null)
        {
            if (a.DeclaringType.IsSubclassOf(b.DeclaringType)) return 1;
            if (b.DeclaringType.IsSubclassOf(a.DeclaringType)) return -1;
        }
        if (left.DisplayOrder.HasValue != right.DisplayOrder.HasValue) return left.DisplayOrder.HasValue ? -1 : 1;
        return StringComparer.InvariantCultureIgnoreCase.Compare(left.Node.Name, right.Node.Name);
    }

    internal static IReadOnlyList<ComponentPropertySection> GetSections(IReadOnlyList<ComponentField> fields) => Sections.GetValue(fields, _ =>
    {
        var result = new List<ComponentPropertySection>();
        foreach (var field in fields.Where(field => string.IsNullOrEmpty(field.DisplayMetadata?.Category)))
            result.Add(new(null, field.DisplayOrder, field.DisplayMetadata?.Expand ?? ExpandRule.Auto, [field]));
        foreach (var group in fields.Where(field => !string.IsNullOrEmpty(field.DisplayMetadata?.Category)).GroupBy(field => field.DisplayMetadata!.Category!))
        {
            var metadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttributes<CategoryOrderAttribute>(group.First().Node.MemberDescriptor.DeclaringType)
                .FirstOrDefault(category => category.Name == group.Key);
            result.Add(new(group.Key, metadata?.Order, metadata?.Expand ?? ExpandRule.Always, group.OrderBy(field => field, FieldComparer).ToArray()));
        }
        result.Sort((left, right) =>
        {
            var order = (left.Order ?? 0).CompareTo(right.Order ?? 0);
            if (order != 0) return order;
            if (left.Category == null && right.Category == null) return CompareFields(left.Fields[0], right.Fields[0]);
            if (left.Order.HasValue != right.Order.HasValue) return left.Order.HasValue ? -1 : 1;
            return StringComparer.InvariantCultureIgnoreCase.Compare(left.Category == null ? left.Fields[0].Node.Name : left.Category + "Category",
                right.Category == null ? right.Fields[0].Node.Name : right.Category + "Category");
        });
        return result.ToArray();
    });
}
