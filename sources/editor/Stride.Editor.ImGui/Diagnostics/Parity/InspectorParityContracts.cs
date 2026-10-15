using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal static class InspectorParityContracts
{
    internal static object Run()
    {
        var events = new List<ParsingEvent> { new Scalar("preserved unknown \u03a9") };
        var unknown = UnloadableObjectInstantiator.CreateUnloadableObject(typeof(object), "Missing.Widget", "Missing.Assembly", "actual loader error \u03a9\r\nsecond line", events);
        var diagnostic = InspectorUnloadableDiagnostic.Read(unknown);
        Require(diagnostic.TypeName == unknown.TypeName && diagnostic.AssemblyName == unknown.AssemblyName && diagnostic.Error == unknown.Error,
            "Unknown-object diagnostics must preserve the original loader fields and Unicode/newlines.");
        Require(ReferenceEquals(events, unknown.ParsingEvents) && unknown.ParsingEvents.Count == 1 && ReferenceEquals(events[0], unknown.ParsingEvents[0]),
            "Displaying an unknown value must not replace its actual parser-event stream.");
        var asset = new AssetItem("Materials/Known", new MaterialAsset());
        var value = ContentAssetReference.Create(typeof(Material), asset);
        var known = InspectorReferenceDiagnostic.Read(value, id => id == asset.Id ? asset : null);
        var missing = InspectorReferenceDiagnostic.Read(value, _ => null);
        var empty = InspectorReferenceDiagnostic.Read(null, _ => null);
        Require(known.State == InspectorReferenceState.Known && known.Id == asset.Id && ReferenceEquals(known.Asset, asset),
            "Reference diagnosis must resolve the actual AssetId to the canonical asset.");
        Require(missing.State == InspectorReferenceState.Missing && missing.Id == asset.Id && missing.Location == asset.Location.FullPath,
            "Missing references must retain their actual identifier and authored URL.");
        Require(empty.State == InspectorReferenceState.None, "A null reference must remain distinct from a broken reference.");
        ContentAssetReference.Validate(typeof(Material), value, false);
        var rejected = false;
        try { ContentAssetReference.Validate(typeof(Stride.Graphics.Texture), value, false); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "An incompatible reference must not be accepted by the typed picker.");
        return new { passed = true, cases = new[] { "native-unloadable-events", "loader-fields", "known-reference", "missing-reference", "null-reference", "typed-reference-rejection" } };
    }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
