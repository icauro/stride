using Hexa.NET.ImGui;
using NVector4 = System.Numerics.Vector4;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class EditorThemes
{
    // Solid red from samples/Particles/ParticlesSample/Resources/StrideLogoRed.png.
    private const uint StrideRed = 0xDA0830;
    public static readonly string[] Names = ["Near-black Graphite (Default)", "Dark Steel", "Divided", "Light Steel Blue (Experimental)"];

    public static void Apply(EditorTheme theme)
    {
        if (theme == EditorTheme.LightSteelBlue) Gui.StyleColorsLight(); else Gui.StyleColorsDark();
        var style = Gui.GetStyle();
        style.TreeLinesSize = 1;
        style.TreeLinesRounding = 0;
        style.Colors[(int)ImGuiCol.TreeLines] = Color(theme == EditorTheme.LightSteelBlue ? 0x858A90u : 0x555B63u);
        // Palette roles follow WPF's theme dictionaries; dimensions belong to
        // the shell and remain independent of the selected color theme.
        var (background, normal, control, accent) = theme switch
        {
            EditorTheme.DarkSteel => (0x3C3C3Eu, 0x4E4E51u, 0x323234u, StrideRed),
            EditorTheme.Divided => (0x383838u, 0x444444u, 0x2A2A2Au, StrideRed),
            EditorTheme.LightSteelBlue => (0xDBDBDBu, 0xD1D5DAu, 0xF5F5F5u, StrideRed),
            _ => (0x101113u, 0x202124u, 0x202226u, StrideRed),
        };
        style.Colors[(int)ImGuiCol.WindowBg] = Color(background);
        style.Colors[(int)ImGuiCol.ChildBg] = Color(background);
        style.Colors[(int)ImGuiCol.PopupBg] = Color(background);
        style.Colors[(int)ImGuiCol.MenuBarBg] = Color(normal);
        style.Colors[(int)ImGuiCol.TitleBg] = Color(normal);
        style.Colors[(int)ImGuiCol.TitleBgActive] = Color(normal);
        style.Colors[(int)ImGuiCol.FrameBg] = Color(control);
        style.Colors[(int)ImGuiCol.Button] = Color(normal);
        if (theme == EditorTheme.ExpressionDark)
        {
            style.FrameBorderSize = 1;
            style.Colors[(int)ImGuiCol.Border] = Color(0x41454B);
            style.Colors[(int)ImGuiCol.Separator] = Color(0x303236);
            style.Colors[(int)ImGuiCol.ScrollbarBg] = Color(control);
            style.Colors[(int)ImGuiCol.ScrollbarGrab] = Color(0x303236);
            style.Colors[(int)ImGuiCol.ScrollbarGrabHovered] = Color(0x45474B);
            style.Colors[(int)ImGuiCol.ScrollbarGrabActive] = Color(0x55575B);
            style.Colors[(int)ImGuiCol.DockingEmptyBg] = Color(background);
            style.Colors[(int)ImGuiCol.TableHeaderBg] = Color(normal);
        }
        style.Colors[(int)ImGuiCol.Header] = Color(normal);
        style.Colors[(int)ImGuiCol.HeaderHovered] = Color(accent);
        style.Colors[(int)ImGuiCol.HeaderActive] = Color(accent);
        style.Colors[(int)ImGuiCol.CheckMark] = Color(accent);
        style.Colors[(int)ImGuiCol.SliderGrab] = Color(accent);
        style.Colors[(int)ImGuiCol.ButtonHovered] = Color(accent);
        style.Colors[(int)ImGuiCol.ButtonActive] = Color(accent);
        style.Colors[(int)ImGuiCol.TextSelectedBg] = Color(accent) with { W = .4f };
        var red = Color(accent);
        var subdued = NVector4.Lerp(Color(normal), red, .28f);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = subdued;
        style.Colors[(int)ImGuiCol.FrameBgActive] = NVector4.Lerp(Color(control), red, .45f);
        style.Colors[(int)ImGuiCol.SliderGrabActive] = red;
        style.Colors[(int)ImGuiCol.Tab] = subdued;
        style.Colors[(int)ImGuiCol.TabHovered] = red;
        style.Colors[(int)ImGuiCol.TabSelected] = red;
        style.Colors[(int)ImGuiCol.TabSelectedOverline] = red;
        style.Colors[(int)ImGuiCol.TabDimmed] = Color(normal);
        style.Colors[(int)ImGuiCol.TabDimmedSelected] = subdued;
        style.Colors[(int)ImGuiCol.TabDimmedSelectedOverline] = red;
        style.Colors[(int)ImGuiCol.SeparatorHovered] = red;
        style.Colors[(int)ImGuiCol.SeparatorActive] = red;
        style.Colors[(int)ImGuiCol.ResizeGrip] = red with { W = .25f };
        style.Colors[(int)ImGuiCol.ResizeGripHovered] = red with { W = .65f };
        style.Colors[(int)ImGuiCol.ResizeGripActive] = red;
        style.Colors[(int)ImGuiCol.DockingPreview] = red with { W = .65f };
        style.Colors[(int)ImGuiCol.NavCursor] = red;
        style.Colors[(int)ImGuiCol.TextLink] = red;
    }

    private static NVector4 Color(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
}
