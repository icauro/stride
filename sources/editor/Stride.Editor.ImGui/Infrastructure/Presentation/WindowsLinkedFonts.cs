using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;

namespace Stride.Editor.ImGui;

/// <summary>Reads installed Windows font-link order and exact font-collection faces.</summary>
internal static class WindowsLinkedFonts
{
    internal sealed record Font(string Path, int Face);
    internal static IEnumerable<Font> Resolve(string family)
    {
        using var links = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontLink\SystemLink");
        if (links?.GetValue(family) is not string[] values) yield break;
        foreach (var value in values)
        {
            var parts = value.Split(','); var file = parts[0].Trim(); if (file.Length == 0) continue;
            var path = Path.IsPathRooted(file) ? file : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
            if (!File.Exists(path)) continue;
            int? face;
            try { face = FindFace(path, parts.Length > 1 ? parts[1].Trim() : null); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { Console.Error.WriteLine("Font link: " + error.Message); continue; }
            if (face is { } index) yield return new(path, index);
        }
    }
    private static int? FindFace(string path, string? family)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        uint Read32() { Span<byte> bytes = stackalloc byte[4]; stream.ReadExactly(bytes); return BinaryPrimitives.ReadUInt32BigEndian(bytes); }
        ushort Read16() { Span<byte> bytes = stackalloc byte[2]; stream.ReadExactly(bytes); return BinaryPrimitives.ReadUInt16BigEndian(bytes); }
        var tag = Read32();
        uint[] offsets;
        if (tag == 0x74746366) // ttcf
        {
            Read32(); var count = Read32();
            if (count == 0 || count > 4096 || count * 4L > stream.Length - stream.Position) throw new InvalidDataException("Invalid installed font collection.");
            offsets = new uint[count]; for (var face = 0; face < offsets.Length; face++) offsets[face] = Read32();
        }
        else offsets = [0];
        if (string.IsNullOrWhiteSpace(family)) return offsets.Length == 1 ? 0 : null;
        for (var face = 0; face < offsets.Length; face++)
        {
            if (offsets[face] > stream.Length - 12) continue;
            stream.Position = offsets[face] + 4; var tables = Read16(); stream.Position += 6;
            for (var table = 0; table < tables; table++)
            {
                if (stream.Position > stream.Length - 16) break;
                var tableTag = Read32(); Read32(); var offset = Read32(); var length = Read32(); var next = stream.Position;
                if (tableTag == 0x6E616D65 && length >= 6 && offset <= stream.Length - length) // name
                {
                    stream.Position = offset; Read16(); var count = Read16(); var strings = Read16();
                    if (count * 12L > length - 6L) throw new InvalidDataException("Invalid installed font name table.");
                    for (var name = 0; name < count; name++)
                    {
                        var platform = Read16(); var encoding = Read16(); Read16(); var id = Read16(); var size = Read16(); var position = Read16(); var following = stream.Position;
                        if ((id is 1 or 4 or 16) && (platform == 0 || platform == 3 && (encoding is 0 or 1 or 10)) && (size & 1) == 0 && strings + (long)position + size <= length)
                        {
                            stream.Position = offset + (long)strings + position; var bytes = new byte[size]; stream.ReadExactly(bytes);
                            if (Encoding.BigEndianUnicode.GetString(bytes).Equals(family, StringComparison.OrdinalIgnoreCase)) return face;
                        }
                        stream.Position = following;
                    }
                }
                stream.Position = next;
            }
        }
        return null;
    }
}
