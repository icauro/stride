using System.Globalization;
using System.Text.RegularExpressions;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>A build source location; line and column use the compiler's one-based coordinates.</summary>
internal sealed record BuildDiagnostic(string Path, int Line, int Column, LogMessageType Level, string Code, string Message)
{
    private static readonly Regex MessagePattern = new(
        @"^\s*(?:\d+>)?(?<origin>.+?):\s*(?<level>warning|error|fatal)(?:\s+(?<code>[^\s:]+))?\s*:\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex PositionPattern = new(
        @"^(?<path>.+)\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static BuildDiagnostic? Parse(string text, string workingDirectory)
    {
        if (text.Length > 262144 || !System.IO.Path.IsPathFullyQualified(workingDirectory)) return null;
        try
        {
            var match = MessagePattern.Match(text);
            if (!match.Success) return null;
            var origin = match.Groups["origin"].Value.Trim();
            var line = 1; var column = 1;
            var position = PositionPattern.Match(origin);
            if (position.Success)
            {
                origin = position.Groups["path"].Value.Trim();
                if (!int.TryParse(position.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out line) || line < 1) return null;
                if (position.Groups["column"].Success
                    && (!int.TryParse(position.Groups["column"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out column) || column < 1)) return null;
            }
            origin = origin.Trim('"');
            // Tool-only origins (CSC, MSBUILD) carry no navigable source file.
            if (string.IsNullOrWhiteSpace(origin) || !System.IO.Path.HasExtension(origin)) return null;
            var message = match.Groups["message"].Value;
            var baseDirectory = workingDirectory;
            // MSBuild appends the emitting project, which can differ from the requested target.
            if (message.EndsWith(']'))
            {
                var bracket = message.LastIndexOf(" [", StringComparison.Ordinal);
                if (bracket >= 0)
                {
                    var project = message[(bracket + 2)..^1];
                    var framework = project.IndexOf("::", StringComparison.Ordinal);
                    if (framework >= 0) project = project[..framework];
                    if (System.IO.Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        project = System.IO.Path.GetFullPath(project, workingDirectory);
                        baseDirectory = System.IO.Path.GetDirectoryName(project)!;
                        message = message[..bracket];
                    }
                }
            }
            var path = System.IO.Path.GetFullPath(origin, baseDirectory);
            var level = match.Groups["level"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase)
                ? LogMessageType.Warning : match.Groups["level"].Value.Equals("fatal", StringComparison.OrdinalIgnoreCase)
                    ? LogMessageType.Fatal : LogMessageType.Error;
            return new(path, line, column, level, match.Groups["code"].Value, message);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or RegexMatchTimeoutException or System.IO.PathTooLongException)
        { return null; }
    }
}
