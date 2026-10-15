using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectWorkspace
{
    internal void RetargetStandalonePackage(Package package, string previous, string next)
    {
        if (package.Container is SolutionProject || !Session.Packages.Contains(package) || !ReferenceEquals(package.Session, Session))
            throw new InvalidOperationException("The standalone package is no longer in this workspace.");
        if (!ReferenceEquals(Package, package)) return;
        if (string.Equals(OpenPath, previous, StringComparison.OrdinalIgnoreCase)) OpenPath = next;
        if (string.Equals(PackagePath, previous, StringComparison.OrdinalIgnoreCase)) PackagePath = next;
        if (string.Equals(BuildProjectPath, previous, StringComparison.OrdinalIgnoreCase)) BuildProjectPath = next;
        Name = package.Meta.Name ?? Path.GetFileNameWithoutExtension(next);
    }
}
