using System.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // On-demand endpoint metadata only; never called by the ordinary frame loop.
    private unsafe object InspectVisualCaptureFontMemory()
    {
        var atlas = Gui.GetIO().Fonts;
        var sources = new List<object>();
        var unique = new Dictionary<nuint, int>();
        long sourceBytes = 0;
        for (var index = 0; index < atlas.Sources.Size; index++)
        {
            var source = atlas.Sources.Data + index;
            var pointer = (nuint)source->FontData;
            var bytes = source->FontDataSize;
            var nameBytes = new ReadOnlySpan<byte>(&source->Name_0, 40);
            var terminator = nameBytes.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(terminator < 0 ? nameBytes : nameBytes[..terminator]);
            if (pointer != 0 && bytes > 0)
            {
                sourceBytes += bytes;
                unique[pointer] = Math.Max(unique.GetValueOrDefault(pointer), bytes);
            }
            sources.Add(new { index, name, fontDataPointer = $"0x{pointer:X}", fontDataBytes = bytes,
                ownedByAtlas = source->FontDataOwnedByAtlas != 0, mergeMode = source->MergeMode != 0,
                face = source->FontNo, sizePixels = source->SizePixels });
        }
        var textures = new List<object>();
        long atlasPixelBytes = 0;
        for (var index = 0; index < atlas.TexList.Size; index++)
        {
            var texture = atlas.TexList[index];
            var bytes = (long)texture.Width * texture.Height * texture.BytesPerPixel;
            var hasPixels = texture.GetPixels() != null;
            if (hasPixels) atlasPixelBytes += bytes;
            textures.Add(new { index, width = texture.Width, height = texture.Height,
                bytesPerPixel = texture.BytesPerPixel, hasCpuPixels = hasPixels, pixelBytes = bytes,
                status = texture.Status.ToString(), registeredTextureId = texture.TexID.Handle });
        }
        return new { fontCount = atlas.Fonts.Size, sourceCount = atlas.Sources.Size, sourceBytes,
            uniqueSourcePointerCount = unique.Count, uniqueSourceBytes = unique.Values.Sum(value => (long)value),
            cpuAtlasPixelBytes = atlasPixelBytes, sources, textures,
            boundary = "Live ImGui atlas source pointers and pixel buffers; excludes font loader internals, allocator overhead, DirectWrite and GPU driver allocations" };
    }
}
