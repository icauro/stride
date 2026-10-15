using Stride.Core.Assets.Templates;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static class ProjectTemplateArtwork
{
    internal static string? Resolve(TemplateDescription description, UFile? image)
    {
        if (image == null || string.IsNullOrEmpty(image.ToString())) return null;
        try
        {
            if (description.FullPath != null)
                return Path.GetFullPath(UPath.Combine(description.FullPath.GetFullDirectory(), image).ToOSPath());
            var path = image.ToOSPath();
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }
}
