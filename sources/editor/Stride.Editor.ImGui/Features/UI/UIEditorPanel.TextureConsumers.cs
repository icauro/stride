namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    internal bool UsesRuntimeTexture(Stride.Graphics.Texture texture)
        => viewport?.UsesRuntimeTexture(texture) == true || recoveryReplacement?.UsesRuntimeTexture(texture) == true;
}
