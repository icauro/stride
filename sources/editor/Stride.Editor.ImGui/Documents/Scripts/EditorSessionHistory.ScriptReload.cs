namespace Stride.Editor.ImGui;

internal sealed partial class EditorSessionHistory
{
    internal Action<ScriptSourceDocument>? ScriptSourceSaved { get; set; }
}
