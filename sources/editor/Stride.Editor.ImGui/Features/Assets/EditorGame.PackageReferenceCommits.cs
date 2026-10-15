using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ContentReferenceCommitPhase BeginPackageReferenceCommitPhase(Package package, PackageSettingAccessor accessor)
    {
        var key = (package, accessor.Key);
        var tree = packageSettingTrees.GetValueOrDefault(key);
        var projection = tree?.ActiveProjection;
        var source = accessor.Get(package);
        var providerVersion = SessionAuthoringHistory?.Extensions?.Version;
        return new(command => command(), command => Execute(() =>
        {
            void ValidateSource()
            {
                if (workspace == null || SessionAuthoringHistory == null || IsAuthoringPipelineUpdating || package.IsReadOnly
                || !workspace.Session.Packages.Contains(package) || !package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
                || SessionAuthoringHistory.Extensions?.Version != providerVersion || !SameSettingValue(accessor, accessor.Get(package), source)
                || tree != null && !ReferenceEquals(packageSettingTrees.GetValueOrDefault(key), tree))
                    throw new InvalidOperationException("The registered package reference changed before assignment.");
            }
            ValidateSource();
            ValidatePendingPropertyStrings();
            ValidateSource();
            var previousProjection = tree?.ActiveProjection;
            try
            {
                if (tree != null) tree.ActiveProjection = projection;
                command();
            }
            finally { if (tree != null) tree.ActiveProjection = previousProjection; }
            CommitPackageSettingEdits(package, accessor);
        }));
    }
}
