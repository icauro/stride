using System.Security.Cryptography;
using System.Text;
using Stride.Assets.SpriteFont;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record FontCompilationSource(string Path, byte[] Bytes);
internal sealed record FontCompilationSources(FontCompilationSource[] Files, string Revision)
{
    public static FontCompilationSources Capture(Asset asset, string assetPath)
    {
        var files = new List<FontCompilationSource>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in FontSourcePaths.Files(asset, assetPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var bytes = new MemoryStream(); stream.CopyTo(bytes); var content = bytes.ToArray();
            hash.AppendData(Encoding.UTF8.GetBytes(path)); hash.AppendData(content);
            files.Add(new(path, content));
        }
        return new(files.ToArray(), Convert.ToHexString(hash.GetHashAndReset()));
    }
    public Asset Stage(Asset asset, string assetPath, string output)
    {
        var staged = AssetCloner.Clone(asset); FontSourcePaths.Resolve(staged, assetPath);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Files.Length; index++)
        {
            var source = Files[index]; var folder = Path.Combine(output, "sources", index.ToString("D3")); Directory.CreateDirectory(folder);
            var name = Path.GetFileName(source.Path);
            if (staged is SpriteFontAsset { FontSource: SystemFontProvider system } font
                && string.Equals(font.FontSource.GetFontPath(), source.Path, StringComparison.OrdinalIgnoreCase))
                name = system.GetFontName() + Path.GetExtension(source.Path);
            var destination = Path.GetFullPath(Path.Combine(folder, name));
            if (!string.Equals(Path.GetDirectoryName(destination), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The font name cannot be represented in an owned compiler source path.");
            File.WriteAllBytes(destination, source.Bytes); replacements.Add(source.Path, destination);
        }
        if (staged is SpriteFontAsset sprite)
        {
            var path = sprite.FontSource.GetFontPath();
            sprite.FontSource = new FileFontProvider { Source = replacements[path], Style = sprite.FontSource.Style };
            if (sprite.FontType is OfflineRasterizedSpriteFontType offline && !string.IsNullOrEmpty(offline.CharacterSet)) offline.CharacterSet = replacements[offline.CharacterSet];
            if (sprite.FontType is SignedDistanceFieldSpriteFontType sdf && !string.IsNullOrEmpty(sdf.CharacterSet)) sdf.CharacterSet = replacements[sdf.CharacterSet];
        }
        else if (staged is PrecompiledSpriteFontAsset precompiled) precompiled.FontDataFile = replacements[precompiled.FontDataFile];
        return staged;
    }
}
