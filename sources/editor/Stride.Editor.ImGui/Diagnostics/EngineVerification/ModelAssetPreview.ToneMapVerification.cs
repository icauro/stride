using Stride.Rendering.Images;

namespace Stride.Editor.ImGui;

internal sealed record PreviewToneMapVerification(bool HadPostEffects, int ToneMaps, float Exposure, float KeyValue,
    bool AutoExposure, bool AutoKeyValue, bool TemporalAdaptation);

internal sealed partial class ModelAssetPreview
{
    internal PreviewToneMapVerification ConfigureVerificationExposure()
    {
        if (previewRenderer.PostEffects == null) return new(false, 0, 0, .18f, false, false, false);
        if (previewRenderer.PostEffects is not PostProcessingEffects effects)
            throw new InvalidOperationException("This preview's native post-effect implementation has no supported deterministic exposure fixture.");
        var toneMaps = effects.ColorTransforms.Transforms.OfType<ToneMap>().ToArray();
        if (toneMaps.Length == 0) throw new InvalidOperationException("The HDR preview fixture has no native tone map to stabilize.");
        foreach (var toneMap in toneMaps)
        {
            toneMap.AutoExposure = false;
            toneMap.AutoKeyValue = false;
            toneMap.TemporalAdaptation = false;
            toneMap.Exposure = 0;
            toneMap.KeyValue = .18f;
        }
        var actual = toneMaps[0];
        return new(true, toneMaps.Length, actual.Exposure, actual.KeyValue, actual.AutoExposure,
            actual.AutoKeyValue, actual.TemporalAdaptation);
    }
}
