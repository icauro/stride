using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal byte[] CapturePlatformUpdateSource()
    {
        CommitEdits();
        if (asset != null && !IsCSharpSource)
        {
            var copy = AssetCloner.Clone(asset); copy.Text = Text;
            using var stream = new MemoryStream(); AssetFileSerializer.Save(stream, copy, sourceItem?.YamlMetadata);
            return stream.ToArray();
        }
        var content = encoding.GetBytes(Text); var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes, 0); content.CopyTo(bytes, preamble.Length); return bytes;
    }
}
