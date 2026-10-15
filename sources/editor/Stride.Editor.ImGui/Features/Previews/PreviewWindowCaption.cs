using System.Globalization;

namespace Stride.Editor.ImGui;

/// <summary>Original preview panel caption with its unchanged native window identity.</summary>
internal static class PreviewWindowCaption
{
    private static string? culture, caption;
    internal static string Text
    {
        get
        {
            var current = CultureInfo.CurrentUICulture.Name;
            if (caption != null && culture == current) return caption;
            var translated = EditorPanels.Preview.DisplayWindowName;
            culture = current; caption = translated;
            return translated;
        }
    }
}
