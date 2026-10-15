using System.Globalization;
using Stride.Core.Assets;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

internal sealed record InspectorUnloadableDiagnostic(string Heading, string Error, string TypeName, string AssemblyName)
{
    internal static InspectorUnloadableDiagnostic Read(IUnloadable value) => new(
        string.Format(CultureInfo.CurrentUICulture, "Unable to load the object of type {0} from assembly {1}", value.TypeName, value.AssemblyName),
        value.Error, value.TypeName, value.AssemblyName);
}

internal enum InspectorReferenceState { None, Known, Missing }
internal sealed record InspectorReferenceDiagnostic(InspectorReferenceState State, AssetId? Id, string? Location, AssetItem? Asset)
{
    internal static InspectorReferenceDiagnostic Read(object? value, Func<AssetId, AssetItem?> resolve)
    {
        if (value == null) return new(InspectorReferenceState.None, null, null, null);
        var reference = ContentAssetReference.Read(value);
        var target = reference == null ? null : resolve(reference.Id);
        return new(target is { IsDeleted: false } ? InspectorReferenceState.Known : InspectorReferenceState.Missing,
            reference?.Id, reference?.Location, target is { IsDeleted: false } ? target : null);
    }
}
