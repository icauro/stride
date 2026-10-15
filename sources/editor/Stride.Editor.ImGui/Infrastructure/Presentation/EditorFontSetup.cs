using System.Globalization;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class EditorFontSetup
{
    internal static unsafe void Initialize()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var primary = Path.Combine(directory, "segoeui.ttf");
        var font = File.Exists(primary) ? Gui.GetIO().Fonts.AddFontFromFileTTF(primary, 17) : default;
        MergeFallbacks(font, 17, "Segoe UI");
    }
    internal static unsafe void MergeFallbacks(ImFontPtr font, float size, string family)
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var hasPrimary = !font.IsNull;
        var preferred = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "ja" => "meiryo.ttc",
            "ko" => "malgun.ttf",
            _ => "msyh.ttc",
        };
        var installed = WindowsLinkedFonts.Resolve(family).Concat(family == "Segoe UI" ? Enumerable.Empty<WindowsLinkedFonts.Font>() : WindowsLinkedFonts.Resolve("Segoe UI"))
            .Concat(new[] { preferred, "msyh.ttc", "meiryo.ttc", "YuGothR.ttc", "malgun.ttf" }.Select(name => new WindowsLinkedFonts.Font(Path.Combine(directory, name), 0)));
        if (family != "Segoe UI") installed = installed.Prepend(new(Path.Combine(directory, "segoeui.ttf"), 0));
        foreach (var fallback in installed.DistinctBy(entry => (entry.Path.ToUpperInvariant(), entry.Face)))
        {
            var path = fallback.Path;
            if (!File.Exists(path)) continue;
            var configuration = Gui.ImFontConfig();
            try
            {
                configuration.MergeMode = hasPrimary;
                configuration.FontNo = checked((uint)fallback.Face);
                if (hasPrimary) configuration.DstFont = font;
                var loaded = Gui.GetIO().Fonts.AddFontFromFileTTF(path, size, configuration);
                if (loaded == null) continue;
                if (!hasPrimary) font = loaded;
                hasPrimary = true;
            }
            finally { Gui.Destroy(configuration); }
        }
    }
}

internal sealed partial class EditorGame
{
    private EditorFontSourcePool? fontSourcePool;
}
