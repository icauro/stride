using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

// Owns immutable native font bytes ONLY after loader contexts have been rebound.
// Must be released after DestroyContext, never during device recovery.
internal sealed unsafe class EditorFontSourcePool
{
    private readonly HashSet<nuint> buffers = [];
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    internal long RetainedBytes { get; private set; }
    internal long ReleasedDuplicateBytes { get; private set; }
    internal bool RollbackRestored { get; private set; }

    internal void Canonicalize(ImFontAtlasPtr atlas, Action? beforeInitialize = null)
    {
        if (Environment.CurrentManagedThreadId != ownerThread || buffers.Count != 0 || atlas.Locked)
            throw new InvalidOperationException("Font source pooling requires an unlocked, unpooled owner-thread atlas.");
        var original = new (nuint Pointer, int Bytes, byte Owned)[atlas.Sources.Size];
        var replacement = new nuint[original.Length];
        var hashes = new Dictionary<(int Size, string Hash), List<int>>();
        var fontState = new (uint Fallback, uint Ellipsis)[atlas.Fonts.Size];
        var all = new Dictionary<nuint, int>();
        for (var index = 0; index < original.Length; index++)
        {
            var source = atlas.Sources.Data + index;
            original[index] = ((nuint)source->FontData, source->FontDataSize, source->FontDataOwnedByAtlas);
            replacement[index] = original[index].Pointer;
            if (source->FontData == null || source->FontDataSize <= 0 || source->FontDataOwnedByAtlas == 0)
                continue;
            all.TryAdd(original[index].Pointer, source->FontDataSize);
            var bytes = new ReadOnlySpan<byte>(source->FontData, source->FontDataSize);
            var key = (source->FontDataSize, Convert.ToHexString(SHA256.HashData(bytes)));
            if (!hashes.TryGetValue(key, out var candidates)) hashes.Add(key, candidates = []);
            var match = candidates.FirstOrDefault(candidate => bytesEqual(candidate, index), -1);
            if (match >= 0) replacement[index] = original[match].Pointer;
            else candidates.Add(index);
        }
        bool bytesEqual(int left, int right) => new ReadOnlySpan<byte>((void*)original[left].Pointer, original[left].Bytes)
            .SequenceEqual(new ReadOnlySpan<byte>((void*)original[right].Pointer, original[right].Bytes));
        if (!replacement.Where((pointer, index) => pointer != original[index].Pointer).Any()) return;
        for (var index = 0; index < atlas.Fonts.Size; index++)
            fontState[index] = (atlas.Fonts[index].FallbackChar, atlas.Fonts[index].EllipsisChar);
        // Prepare the failure owner before native mutation; no buffer is freed until ALL sources initialize.
        foreach (var pair in all) buffers.Add(pair.Key);
        RetainedBytes = all.Values.Sum(value => (long)value);
        void DestroyLoaders()
        {
            for (var index = 0; index < atlas.Fonts.Size; index++)
            {
                ImGuiP.ImFontAtlasFontDestroyOutput(atlas, atlas.Fonts[index]);
                atlas.Fonts[index].FallbackChar = fontState[index].Fallback;
                atlas.Fonts[index].EllipsisChar = fontState[index].Ellipsis;
            }
        }
        void InitializeLoaders()
        {
            for (var index = 0; index < atlas.Fonts.Size; index++)
                if (!ImGuiP.ImFontAtlasFontInitOutput(atlas, atlas.Fonts[index]))
                    throw new InvalidOperationException("Native font loader rejected unchanged pooled font data.");
        }
        try
        {
            // ImGui 1.92.2b: destroys ALL baked font contexts, then each FontSrcDestroy.
            DestroyLoaders();
            for (var index = 0; index < original.Length; index++)
            {
                var source = atlas.Sources.Data + index;
                source->FontData = (void*)replacement[index];
                if (all.ContainsKey(original[index].Pointer)) source->FontDataOwnedByAtlas = 0;
            }
            beforeInitialize?.Invoke();
            InitializeLoaders();
        }
        catch (Exception poolingFailure)
        {
            try
            {
                DestroyLoaders();
                for (var index = 0; index < original.Length; index++)
                {
                    var source = atlas.Sources.Data + index;
                    source->FontData = (void*)original[index].Pointer;
                    source->FontDataOwnedByAtlas = original[index].Owned;
                }
                InitializeLoaders();
                buffers.Clear(); RetainedBytes = 0; RollbackRestored = true;
            }
            catch (Exception rollbackFailure)
            {
                // Keep every original byte buffer alive; context destruction releases loader borrows first.
                for (var index = 0; index < original.Length; index++)
                    if (all.ContainsKey((nuint)atlas.Sources[index].FontData)) atlas.Sources.Data[index].FontDataOwnedByAtlas = 0;
                throw new AggregateException("Native font pooling and rollback failed; original buffers remain retained until context destruction.", poolingFailure, rollbackFailure);
            }
            throw;
        }
        var retained = replacement.ToHashSet();
        foreach (var pointer in buffers.ToArray())
            if (!retained.Contains(pointer))
            {
                Gui.MemFree((void*)pointer); buffers.Remove(pointer);
                ReleasedDuplicateBytes += all[pointer]; RetainedBytes -= all[pointer];
            }
    }

    internal void ReleaseAfterContextDestroyed()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Native font bytes belong to their actual context owner thread.");
        foreach (var pointer in buffers) Gui.MemFree((void*)pointer);
        buffers.Clear(); RetainedBytes = 0;
    }
}
