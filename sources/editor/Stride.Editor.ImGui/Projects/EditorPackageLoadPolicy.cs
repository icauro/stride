using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>The native companion boundary for editor-owned sessions, never compiler sessions.</summary>
internal static class EditorPackageLoadPolicy
{
    internal const string Toolkit = "ImGui";

    internal static PackageSession CreateSession()
        => new() { LoadEditorPackages = true, EditorToolkit = Toolkit };
}
