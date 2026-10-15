using Stride.Core.Quantum;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

// Native unknown objects serialize their original event stream, not edited base
// fields. Preserve that stream by permitting edits only outside the proxy subtree.
internal static class UnknownAssetData
{
    internal static bool IsProxyNode(IGraphNode node)
        => node is IMemberNode member ? member.Parent.Retrieve() is IUnloadable
            : node is IObjectNode value && value.Retrieve() is IUnloadable;
}
