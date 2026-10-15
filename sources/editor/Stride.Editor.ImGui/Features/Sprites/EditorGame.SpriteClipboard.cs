using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string spriteClipboardText = "";
    private uint spriteClipboardSequence;
    private long spriteClipboardFields = -1;
    private AssetMetadataDocument? spriteClipboardDocument;
    private bool canPasteSprites;
    private void CopySpriteFrames(AssetMetadataDocument document)
    {
        var text = document.CopySelectedSpriteFrames();
        if (text.Length == 0) return;
        Gui.SetClipboardText(text);
        if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The sprites could not be written to the clipboard.");
        spriteClipboardDocument = null;
    }
    private bool CanPasteSprites(AssetMetadataDocument document)
    {
        var sequence = clipboard?.ChangeSequence ?? 0;
        if (spriteClipboardSequence != sequence || !ReferenceEquals(spriteClipboardDocument, document) || spriteClipboardFields != document.SpriteFieldsVersion)
        {
            spriteClipboardSequence = sequence; spriteClipboardDocument = document; spriteClipboardFields = document.SpriteFieldsVersion; spriteClipboardText = Gui.GetClipboardTextS() ?? "";
            try { canPasteSprites = document.CanPasteSpriteFrames(spriteClipboardText); }
            catch (SystemException) { canPasteSprites = false; }
        }
        return document.CanEditSpriteCollection && canPasteSprites;
    }
    private void PasteSpriteFrames(AssetMetadataDocument document)
    {
        spriteClipboardDocument = null; // Re-read on the explicit action after transient clipboard contention.
        if (!CanPasteSprites(document)) return;
        document.PasteSpriteFrames(spriteClipboardText);
    }
    private void ClearSpriteClipboardState()
    { spriteClipboardText = ""; spriteClipboardDocument = null; spriteClipboardSequence = 0; spriteClipboardFields = -1; canPasteSprites = false; }
}
