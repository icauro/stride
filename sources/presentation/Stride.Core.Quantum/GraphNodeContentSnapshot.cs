using System.Collections;
using Stride.Core.Reflection;

namespace Stride.Core.Quantum;

/// <summary>Retains actual node contents for restoring a mutation that failed before its completion event.</summary>
public sealed class GraphNodeContentSnapshot
{
    private readonly Action restore;

    public GraphNodeContentSnapshot(IGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is MemberNode member)
        {
            var value = member.Retrieve();
            restore = () => member.RestoreContentWithoutNotification(value);
            return;
        }
        if (node is not GraphNodeBase actual || node.Retrieve() is not { } contents)
            throw new ArgumentException("The node has no restorable content.", nameof(node));
        switch (node.Descriptor)
        {
            case DictionaryDescriptor dictionary:
                var entries = dictionary.GetEnumerator(contents).ToArray();
                restore = () =>
                {
                    var current = dictionary.GetEnumerator(contents).ToArray();
                    if (current.Length != entries.Length || entries.Any(entry => !current.Any(pair => Equals(pair.Key, entry.Key) && SameValue(pair.Value, entry.Value))))
                    {
                        dictionary.Clear(contents);
                        foreach (var entry in entries) dictionary.AddToDictionary(contents, entry.Key, entry.Value);
                    }
                    actual.RefreshContentReferences();
                };
                break;
            case CollectionDescriptor collection:
                var items = ((IEnumerable)contents).Cast<object?>().ToArray();
                restore = () =>
                {
                    var current = ((IEnumerable)contents).Cast<object?>().ToArray();
                    if (current.Length != items.Length || items.Where((item, index) => index >= current.Length || !SameValue(item, current[index])).Any())
                    {
                        collection.Clear(contents);
                        foreach (var item in items) collection.Add(contents, item);
                    }
                    actual.RefreshContentReferences();
                };
                break;
            case ArrayDescriptor array:
                var values = Enumerable.Range(0, array.GetLength(contents)).Select(index => array.GetValue(contents, index)).ToArray();
                restore = () =>
                {
                    for (var index = 0; index < values.Length; index++) array.SetValue(contents, index, values[index]);
                    actual.RefreshContentReferences();
                };
                break;
            default:
                throw new ArgumentException("The node is not a member or collection.", nameof(node));
        }
    }

    /// <summary>Restores retained object references and refreshes real Quantum references, without another edit event.</summary>
    public void Restore() => restore();

    internal static bool SameValue(object? left, object? right)
        => ReferenceEquals(left, right) || left != null && right != null && (left.GetType().IsValueType || left is string) && Equals(left, right);
}
