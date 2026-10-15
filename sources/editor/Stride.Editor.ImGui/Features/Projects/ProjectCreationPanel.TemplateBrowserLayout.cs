using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private float templateGroupFraction = .25f;
    private float templateDetailsWidth = 220;
    private readonly EditorSplitterState templateGroupSplitter = new();
    private readonly EditorSplitterState templateDetailsSplitter = new();
    private readonly record struct TemplateBrowserLayout(float Groups, float Templates, float Details, float Splitter, float Height);

    private TemplateBrowserLayout ResolveTemplateBrowserLayout()
    {
        var scale = Gui.GetStyle().FontScaleDpi;
        var available = Gui.GetContentRegionAvail();
        var splitter = 5 * scale;
        var width = Math.Max(0, available.X - splitter * 2);
        var details = Math.Clamp(templateDetailsWidth * scale, 0, width);
        var lists = width - details;
        var groupsWidth = lists * Math.Clamp(templateGroupFraction, 0, 1);
        var solutionFields = selectionSource == null ? 0 : 2 * Gui.GetFrameHeightWithSpacing();
        return new(groupsWidth, lists - groupsWidth, details, splitter, Math.Max(1, available.Y - 100 * scale - solutionFields - ProjectSelectionFooterExtraHeight(available.X)));
    }

    private void DrawTemplateGroupSplitter(Vector2 origin, TemplateBrowserLayout layout)
    {
        var scale = Gui.GetStyle().FontScaleDpi;
        var extent = (layout.Groups + layout.Templates) / scale;
        Gui.SetCursorScreenPos(origin + new Vector2(layout.Groups, 0));
        EditorSplitter.Draw("##TemplateGroupSplitter", new Vector2(layout.Splitter, layout.Height), true, true,
            templateGroupSplitter, ref templateGroupFraction, layout.Groups / scale, extent, 0, extent);
    }

    private void DrawTemplateDetailsSplitter(Vector2 origin, TemplateBrowserLayout layout)
    {
        var scale = Gui.GetStyle().FontScaleDpi;
        var extent = (layout.Templates + layout.Details) / scale;
        var listWidth = layout.Templates / scale;
        Gui.SetCursorScreenPos(origin + new Vector2(layout.Groups + layout.Splitter + layout.Templates, 0));
        if (!EditorSplitter.Draw("##TemplateDetailsSplitter", new Vector2(layout.Splitter, layout.Height), true, false,
            templateDetailsSplitter, ref listWidth, listWidth, extent, 0, extent)) return;
        templateDetailsWidth = extent - listWidth;
        var groupsWidth = layout.Groups / scale;
        var listsWidth = groupsWidth + listWidth;
        templateGroupFraction = listsWidth > 0 ? groupsWidth / listsWidth : .25f;
    }
}
