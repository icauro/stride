using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record ProjectSolutionSettings(string Name, string Location);

internal static partial class ProjectTemplateService
{
    internal static string ValidateSolutionDestination(string name, string location, ProjectSolutionSettings solution)
    {
        var project = ValidateDestination(name, location);
        if (!string.IsNullOrWhiteSpace(solution.Location) && !UPath.IsValid(solution.Location))
            throw new ArgumentException("Invalid solution directory.");
        if (!string.IsNullOrWhiteSpace(solution.Name) && (!UFile.IsValid(solution.Name) || solution.Name != Path.GetFileName(solution.Name)
            || solution.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || solution.Name is "." or ".." || solution.Name.TrimEnd(' ', '.') != solution.Name))
            throw new ArgumentException("Invalid solution name.");
        var solutionName = string.IsNullOrWhiteSpace(solution.Name) ? name : solution.Name;
        if (!solutionName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) solutionName += ".slnx";
        string directory;
        try { directory = string.IsNullOrWhiteSpace(solution.Location) ? project : Path.GetFullPath(solution.Location); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Invalid solution directory.", failure); }
        var path = Path.GetFullPath(Path.Combine(directory, solutionName));
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("Cannot use the selected solution name because a file or folder with the same name already exists in the same location.");
        return path;
    }
}
