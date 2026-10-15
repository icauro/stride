using Stride.GameStudio.Helpers;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // WPF DialogService uses the registered editor application name for message boxes.
    private static string WpfMessageDialogTitle(string popupId) => StrideGameStudio.EditorName + "###" + popupId;
}
