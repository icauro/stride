using System;
using System.Collections.Generic;
using System.IO;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Assets.SpriteFont;

/// <summary>Resolves font compiler sources on a snapshot without changing the authored graph.</summary>
public static class FontSourcePaths
{
    public static void Resolve(Asset snapshot, string assetPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(assetPath))!;
        UFile Absolute(UFile value) => string.IsNullOrEmpty(value) ? value : new UFile(Path.GetFullPath(value, parent));
        if (snapshot is SpriteFontAsset font)
        {
            if (font.FontSource is FileFontProvider file) file.Source = Absolute(file.Source);
            if (font.FontType is OfflineRasterizedSpriteFontType offline) offline.CharacterSet = Absolute(offline.CharacterSet);
            if (font.FontType is SignedDistanceFieldSpriteFontType sdf) sdf.CharacterSet = Absolute(sdf.CharacterSet);
        }
        else if (snapshot is PrecompiledSpriteFontAsset precompiled) precompiled.FontDataFile = Absolute(precompiled.FontDataFile);
        else throw new ArgumentException("The asset is not a sprite font.");
    }
    public static IReadOnlyList<string> Files(Asset snapshot, string assetPath)
    {
        var copy = AssetCloner.Clone(snapshot); Resolve(copy, assetPath);
        var paths = new List<string>();
        void Add(string path) { if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) paths.Add(path); }
        if (copy is SpriteFontAsset font)
        {
            Add(font.FontSource?.GetFontPath());
            if (font.FontType is OfflineRasterizedSpriteFontType offline) Add(offline.CharacterSet);
            if (font.FontType is SignedDistanceFieldSpriteFontType sdf) Add(sdf.CharacterSet);
        }
        else if (copy is PrecompiledSpriteFontAsset precompiled) Add(precompiled.FontDataFile);
        return paths;
    }
}
