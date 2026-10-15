namespace Stride.Editor.ImGui;

internal sealed class ProjectTemplateGroup(string name, string path)
{
    internal string Name { get; } = name;
    internal string Path { get; } = path;
    internal List<ProjectTemplateGroup> Children { get; } = [];
    internal List<ProjectTemplateChoice> Templates { get; } = [];
    internal IEnumerable<ProjectTemplateChoice> Descendants => Templates.Concat(Children.SelectMany(child => child.Descendants));

    internal ProjectTemplateGroup? Find(string path)
        => Path == path ? this : Children.Select(child => child.Find(path)).FirstOrDefault(child => child != null);

    internal static ProjectTemplateGroup Create(IEnumerable<ProjectTemplateChoice> choices, bool existingSolution)
    {
        var root = new ProjectTemplateGroup(existingSolution ? "All templates" : "New project", "");
        var general = new ProjectTemplateGroup("General", "General/");
        root.Children.Add(general);
        foreach (var choice in choices)
        {
            var current = root;
            var names = choice.Description.Group?.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (names.Length == 0) current = general;
            else foreach (var name in names)
            {
                var child = current.Children.FirstOrDefault(child => child.Name == name);
                if (child == null)
                {
                    child = new ProjectTemplateGroup(name, current.Path + name + "/");
                    current.Children.Add(child);
                }
                current = child;
            }
            current.Templates.Add(choice);
        }
        Sort(root);
        return root;
    }

    private static void Sort(ProjectTemplateGroup group)
    {
        group.Children.Sort((left, right) => StringComparer.InvariantCultureIgnoreCase.Compare(left.Name, right.Name));
        foreach (var child in group.Children) Sort(child);
    }
}
