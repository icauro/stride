using System;
using System.Reflection;
using Stride.Core.Annotations;
using Stride.Core.Reflection;

namespace Stride.Core.Assets.Editor.Quantum.NodePresenters.Rules;

internal static class InlineMemberRules
{
    internal static (string? Name, ExpandRule Expand) FindMainProperty(Type type)
    {
        string? mainProperty = null;
        var expand = ExpandRule.Never;
        foreach (var property in type.GetProperties())
        {
            var attribute = property.GetCustomAttribute<InlinePropertyAttribute>();
            if (attribute == null) continue;
            if (mainProperty != null) throw new InvalidOperationException("Multiple properties of the same node have the InlinePropertyAttribute.");
            mainProperty = property.Name;
            expand = attribute.Expand;
        }
        return (mainProperty, expand);
    }
}
