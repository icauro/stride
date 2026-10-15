using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal virtual PropertyNumericRange? ExtensionNumericRange(IMemberNode member) => ResolveExtensionPresentation(member)?.NumericRange(member);
    internal virtual bool ExtensionNumericAxisReadOnly(IMemberNode member, int axis)
        => ResolveExtensionPresentation(member)?.Find(member) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal virtual PropertyNumericRange? ExtensionNumericRange(VirtualNodePresenter presenter)
        => ResolveExtensionPresentation(presenter)?.NumericRange(presenter);
    internal virtual IReadOnlyList<PropertyNumericRange?> ExtensionAxisNumericRanges(IMemberNode member)
    {
        var fallback = VectorPropertyNumericMetadata.Create(member);
        return ResolveExtensionPresentation(member) is { } presentation && presentation.Find(member) is { } presenter
            ? presentation.NumericAxisRanges(presenter, fallback) : fallback;
    }
}
