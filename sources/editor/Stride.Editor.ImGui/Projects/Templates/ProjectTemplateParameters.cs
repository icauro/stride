using Microsoft.TemplateEngine.Abstractions;
using Stride.Assets.Templates;

namespace Stride.Editor.ImGui;

/// <summary>Projects the registered template parameters using the native dialog's value rules.</summary>
internal static class ProjectTemplateParameters
{
    internal static IEnumerable<ITemplateParameter> Visible(ITemplateInfo template)
    {
        var hidden = DotNetNewTemplateBridge.GetHiddenParameterNames(template);
        return template.ParameterDefinitions.Where(parameter => parameter.Type == "parameter"
            && !parameter.IsName && parameter.Name != "name" && !hidden.Contains(parameter.Name)
            && !(IsChoice(parameter) && parameter.Choices is { Count: <= 1 }));
    }

    internal static bool IsChoice(ITemplateParameter parameter)
        => string.Equals(parameter.DataType, "choice", StringComparison.OrdinalIgnoreCase);

    internal static string InitialValue(ITemplateParameter parameter)
    {
        if (string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase))
            return bool.TryParse(parameter.DefaultValue, out var enabled) && enabled ? "true" : "false";
        if (IsChoice(parameter) && parameter.Choices is { Count: > 0 } options)
        {
            if (!parameter.AllowMultipleValues)
                return options.ContainsKey(parameter.DefaultValue ?? "") ? parameter.DefaultValue! : options.Keys.First();
            var defaults = (parameter.DefaultValue ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (parameter.Name == "platforms" && defaults.Remove("host"))
                defaults.Add(OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "windows");
            return string.Join("|", options.Keys.Where(option => !(parameter.Name == "platforms" && option == "host")
                && defaults.Contains(option)));
        }
        return parameter.DefaultValue ?? "";
    }

    internal static Dictionary<string, string> Capture(ITemplateInfo template, IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in Visible(template))
        {
            if (!values.TryGetValue(parameter.Name, out var value)) continue;
            var hasChoices = IsChoice(parameter) && parameter.Choices is { Count: > 0 };
            var isBoolean = string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase);
            // The original dialog lets the engine apply defaults for empty free
            // text/empty multi-selection and omits all unchanged default values.
            if (value.Length == 0 && (!hasChoices && !isBoolean || hasChoices && parameter.AllowMultipleValues)) continue;
            if (value != parameter.DefaultValue) result.Add(parameter.Name, value);
        }
        return result;
    }
}
