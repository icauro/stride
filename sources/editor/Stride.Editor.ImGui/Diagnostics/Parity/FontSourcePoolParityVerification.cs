using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static unsafe class FontSourcePoolParityVerification
{
    internal static object Verify()
    {
        var previous = Gui.GetCurrentContext();
        var evidence = new List<object>();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var context = Gui.CreateContext();
            var pool = new EditorFontSourcePool();
            try
            {
                Gui.SetCurrentContext(context);
                var atlas = Gui.GetIO().Fonts;
                var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                var primary = Path.Combine(directory, "segoeui.ttf");
                if (!File.Exists(primary)) throw new InvalidOperationException("Native font pooling proof requires installed Segoe UI.");
                var ui = atlas.AddFontFromFileTTF(primary, 17);
                var script = atlas.AddFontFromFileTTF(primary, 12);
                var cjk = Path.Combine(directory, "msyh.ttc");
                var faces = 0;
                if (File.Exists(cjk))
                {
                    for (uint face = 0; face < 2; face++)
                    {
                        var config = Gui.ImFontConfig();
                        try { config.MergeMode = true; config.DstFont = script; config.FontNo = face;
                            if (atlas.AddFontFromFileTTF(cjk, 12, config) == null) throw new InvalidOperationException("Native CJK face unavailable."); }
                        finally { Gui.Destroy(config); }
                        faces++;
                    }
                }
                var originals = new nuint[atlas.Sources.Size];
                var sourceMetadata = new (uint Face, float Size, byte Merge)[originals.Length];
                for (var index = 0; index < originals.Length; index++)
                {
                    var source = atlas.Sources.Data + index;
                    originals[index] = (nuint)source->FontData;
                    sourceMetadata[index] = (source->FontNo, source->SizePixels, source->MergeMode);
                }
                var before = GlyphMetrics(ui, 17).Concat(GlyphMetrics(script, 12)).ToArray();
                try { pool.Canonicalize(atlas, () => throw new IOException("Injected post-rebind transaction failure"));
                    throw new InvalidOperationException("Injected transaction did not fail."); }
                catch (IOException) { }
                if (!pool.RollbackRestored || pool.RetainedBytes != 0) throw new InvalidOperationException("Font rollback did not restore native ownership.");
                for (var index = 0; index < originals.Length; index++)
                    if ((nuint)atlas.Sources[index].FontData != originals[index] || atlas.Sources[index].FontDataOwnedByAtlas == 0)
                        throw new InvalidOperationException("Rollback changed an actual original font buffer owner.");
                if (!before.SequenceEqual(GlyphMetrics(ui, 17).Concat(GlyphMetrics(script, 12))))
                    throw new InvalidOperationException("Rollback changed actual native glyph metrics.");
                pool.Canonicalize(atlas);
                if (atlas.Sources[0].FontData != atlas.Sources[1].FontData || pool.ReleasedDuplicateBytes <= 0)
                    throw new InvalidOperationException("Equal native font bytes were not genuinely shared.");
                for (var index = 0; index < sourceMetadata.Length; index++)
                {
                    var source = atlas.Sources.Data + index;
                    if (sourceMetadata[index] != (source->FontNo, source->SizePixels, source->MergeMode))
                        throw new InvalidOperationException("Pooling changed native source face/size/fallback order metadata.");
                }
                for (var repeat = 0; repeat < 3; repeat++)
                    if (!before.SequenceEqual(GlyphMetrics(ui, 17).Concat(GlyphMetrics(script, 12))))
                        throw new InvalidOperationException("Pooling changed actual glyph advances or ink bounds.");
                evidence.Add(new { iteration, sourceCount = originals.Length, cjkFaces = faces,
                    retainedBytes = pool.RetainedBytes, releasedDuplicateBytes = pool.ReleasedDuplicateBytes,
                    rollbackRestored = pool.RollbackRestored, repeatedGlyphMetricsEqual = true });
            }
            finally
            {
                Gui.DestroyContext(context);
                pool.ReleaseAfterContextDestroyed();
                Gui.SetCurrentContext(previous);
            }
            if (pool.RetainedBytes != 0) throw new InvalidOperationException("Destroyed private context retained pooled font sources.");
        }
        return new { privateNativeContexts = 2, evidence, contextDestroyedBeforePoolRelease = true };
    }

    private static float[] GlyphMetrics(ImFontPtr font, float size)
    {
        var baked = new ImFontBakedPtr(font.GetFontBaked(size));
        var metrics = new List<float>();
        foreach (var character in "AWgΩЖ中文")
        {
            var glyph = baked.FindGlyphNoFallback(character);
            metrics.Add(glyph == null ? -1 : glyph->AdvanceX);
            if (glyph != null) { metrics.Add(glyph->X0); metrics.Add(glyph->Y0); metrics.Add(glyph->X1); metrics.Add(glyph->Y1); }
        }
        return metrics.ToArray();
    }
}
