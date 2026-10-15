using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    private EditorAssetPropertyPresentation? numericPresentation;
    private long numericPresentationVersion = -1;
    private EditorAssetPropertyPresentation? NumericPresentation
    {
        get
        {
            var extensions = SessionHistory?.Extensions;
            if (extensions == null) return null;
            if (numericPresentationVersion != extensions.Version)
            {
                numericPresentation?.Dispose(); numericPresentation = null;
                numericPresentation = new(AuthoringGraph, extensions);
                numericPresentationVersion = extensions.Version;
            }
            return numericPresentation;
        }
    }
    internal PropertyNumericRange? NumericRange(IMemberNode member)
        => NumericPresentation?.NumericRange(member) ?? AssetSettingNumericMetadata.Read(member);
    internal IReadOnlyList<PropertyNumericRange?> AxisNumericRanges(IMemberNode member)
    {
        var fallback = VectorPropertyNumericMetadata.Create(member);
        return NumericPresentation is { } presentation && presentation.Find(member) is { } presenter
            ? presentation.NumericAxisRanges(presenter, fallback) : fallback;
    }
    internal bool NumericReadOnly(IMemberNode member, int axis = -1)
        => NumericPresentation is { } presentation && (presentation.IsReadOnly(member)
            || axis >= 0 && presentation.Find(member) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis));
    internal void DisposeNumericPresentation()
    { numericPresentation?.Dispose(); numericPresentation = null; numericPresentationVersion = -1; }
}
