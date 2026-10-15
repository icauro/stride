using System.Text;

namespace Stride.Assets.Templates;

public static class DotNetNewTemplatePresentation
{
    public static string Humanize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var result = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character is '-' or '_')
            {
                if (result.Length > 0 && result[result.Length - 1] != ' ') result.Append(' ');
                continue;
            }
            if (index > 0 && char.IsUpper(character) && result.Length > 0 && result[result.Length - 1] != ' ')
            {
                var previous = name[index - 1];
                var next = index + 1 < name.Length ? name[index + 1] : '\0';
                if (char.IsLower(previous) || char.IsDigit(previous) || char.IsUpper(previous) && char.IsLower(next)) result.Append(' ');
            }
            result.Append(result.Length == 0 || result[result.Length - 1] == ' ' ? char.ToUpperInvariant(character) : character);
        }
        return result.ToString();
    }
}
