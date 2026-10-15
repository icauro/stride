using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private CurveEditorPanel? curveEditor;
    internal void OpenCurveEditor(EntityHierarchyDocument owner, IMemberNode node, string title)
        => Execute(() => { ValidatePendingPropertyStrings(); if (owner.BindCurve(node, title) is { } binding) GetCurveEditor().Open(binding); });
    internal void OpenCurveEditor(AssetMetadataDocument owner, IMemberNode node, string title)
        => Execute(() => { ValidatePendingPropertyStrings(); if (owner.BindCurve(node, title) is { } binding) GetCurveEditor().Open(binding); });
    private CurveEditorPanel GetCurveEditor() => curveEditor ??= new(message => statusMessage = message,
        (identity, text, selected) => DrawNativeLiteralSelectable(identity, text, selected),
        ValidatePendingPropertyStrings, DrawCurveNumericDraft,
        (identity, caption, flags) => DrawNativeTreeNode(identity, flags, true, out _, caption: caption));
    private void DrawCurveEditorPanel() => curveEditor?.Draw();
    private void CloseCurveEditor() { curveEditor?.Dispose(); curveEditor = null; }
}
