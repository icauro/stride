using Hexa.NET.ImGui;
using Stride.Core.Presentation.Extensions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetCreationPanel
{
    private sealed class TemplateGroup(string name, string id)
    {
        internal string Name { get; } = name;
        internal string Id { get; } = id;
        internal List<TemplateGroup> Children { get; } = [];
        internal List<Choice> Templates { get; } = [];
        internal IEnumerable<Choice> Descendants() => Templates.Concat(Children.SelectMany(child => child.Descendants()));
    }
    private readonly TemplateGroup templateRoot = new("All templates", "root");
    private TemplateGroup? selectedTemplateGroup;
    private bool templateNameDefined, templateChoicesDirty = true;
    private Choice[] visibleTemplateChoices = [];
    private TemplateGroup? hoveredTemplateGroup;
    private double templateGroupHoverStart;
    private int templateGroupHoverFrame;

    private void BuildTemplateGroups()
    {
        ClearTemplateGroups();
        foreach (var choice in choices)
        {
            if (choice.Template == null) { templateRoot.Templates.Add(choice); continue; }
            var path = choice.Template.Group;
            if (string.IsNullOrWhiteSpace(path)) continue;
            var group = templateRoot;
            foreach (var part in path.Split(['/', '\\']))
            {
                var child = group.Children.FirstOrDefault(candidate => candidate.Name == part);
                if (child == null)
                {
                    child = new(part, group.Id + "/" + part.Length + ":" + part);
                    group.Children.Add(child);
                }
                group = child;
            }
            group.Templates.Add(choice);
        }
        SortGroups(templateRoot);
    }
    private static void SortGroups(TemplateGroup group)
    {
        group.Children.Sort((left, right) => StringComparer.InvariantCultureIgnoreCase.Compare(left.Name, right.Name));
        foreach (var child in group.Children) SortGroups(child);
    }
    private void ClearTemplateGroups()
    {
        templateRoot.Children.Clear(); templateRoot.Templates.Clear(); selectedTemplateGroup = null; templateNameDefined = false;
        visibleTemplateChoices = []; templateChoicesDirty = true; hoveredTemplateGroup = null;
    }
    private Choice[] VisibleTemplateChoices()
    {
        if (!templateChoicesDirty) return visibleTemplateChoices;
        var tokens = filter.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        visibleTemplateChoices = (selectedTemplateGroup ?? templateRoot).Descendants()
            .Where(choice => tokens.All(token => choice.Name.IndexOf(token, StringComparison.InvariantCultureIgnoreCase) >= 0 || token.MatchCamelCase(choice.Name)))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCulture).ToArray();
        templateChoicesDirty = false; return visibleTemplateChoices;
    }
    private void SelectTemplateChoice(Choice? choice)
    {
        var suggestion = name;
        if (!templateNameDefined)
            suggestion = choice == null || choice.Template?.RequireName == false ? ""
                : suggestTemplateName != null ? suggestTemplateName(choice) ?? ""
                : choice.Template?.DefaultOutputName ?? choice.Template?.AssetTypeName ?? choice.Name;
        if (!ReferenceEquals(selected, choice)) RetainTemplateReference(null);
        selected = choice; name = suggestion;
        if (choice != null) sourceNamespace = choice.DefaultNamespace ?? sourceNamespace;
        SelectReferences();
    }
    private void DrawTemplateGroups()
    {
        using var child = ImGuiScopes.Child("##AssetTemplateGroups", new System.Numerics.Vector2(160, 240) * Gui.GetStyle().FontScaleDpi, ImGuiChildFlags.Borders, ImGuiWindowFlags.None);
        if (!child.DrawContents) return;
        if (templateGroupHoverFrame < Gui.GetFrameCount() - 1) hoveredTemplateGroup = null;
        using var disabled = ImGuiScopes.Disabled(filter.Length != 0);
        foreach (var group in templateRoot.Children) DrawTemplateGroup(group);
    }
    private void DrawTemplateGroup(TemplateGroup group)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (ReferenceEquals(group, selectedTemplateGroup)) flags |= ImGuiTreeNodeFlags.Selected;
        if (group.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var open = Gui.TreeNodeEx(group.Name + "###AssetTemplateGroup/" + group.Id, flags);
        var choose = Gui.IsItemClicked() && !Gui.IsItemToggledOpen();
        if (filter.Length == 0 && Gui.IsItemHovered())
        {
            if (!ReferenceEquals(hoveredTemplateGroup, group))
            { hoveredTemplateGroup = group; templateGroupHoverStart = Gui.GetTime(); }
            templateGroupHoverFrame = Gui.GetFrameCount();
            choose |= Gui.GetTime() - templateGroupHoverStart >= 0.2;
        }
        if (choose && !ReferenceEquals(selectedTemplateGroup, group))
        {
            selectedTemplateGroup = group; templateChoicesDirty = true;
            if (selected != null && !VisibleTemplateChoices().Contains(selected)) SelectTemplateChoice(null);
        }
        if (open && group.Children.Count != 0)
        {
            foreach (var child in group.Children) DrawTemplateGroup(child);
            Gui.TreePop();
        }
    }
}
