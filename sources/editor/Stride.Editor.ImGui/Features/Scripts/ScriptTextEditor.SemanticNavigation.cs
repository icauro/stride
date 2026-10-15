using System.Numerics;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private ScriptBracePair[] bracePairs = [];
    private long braceVersion = -1, braceRequestedVersion = -1;
    private int braceRequestedPosition = -1;
    private int hoveredSourcePosition = -1, quickInfoRequestedPosition = -1;
    private long hoverChangedAt, quickInfoVersion = -1, hoverSourceVersion = -1;
    private string quickInfoText = "", completionDescription = "";
    private int describedCompletion = -1;
    private readonly EditorSelectableText semanticInfoText = new();
    private bool CompletionCommitsCharacter(char character)
    {
        if (completion == null || completion.SuggestionMode || selectedCompletion >= completion.Items.Length) return false;
        var allowed = completion.Rules.DefaultCommitCharacters.Contains(character);
        foreach (var rule in completion.Items[selectedCompletion].Rules.CommitCharacterRules)
            allowed = rule.Kind switch
            {
                CharacterSetModificationKind.Add => allowed || rule.Characters.Contains(character),
                CharacterSetModificationKind.Remove => allowed && !rule.Characters.Contains(character),
                CharacterSetModificationKind.Replace => rule.Characters.Contains(character), _ => allowed
            };
        return allowed;
    }
    private bool CompletionCommitsEnter()
    {
        if (completion == null || completion.SuggestionMode || selectedCompletion >= completion.Items.Length) return false;
        var item = completion.Items[selectedCompletion]; var rule = item.Rules.EnterKeyRule;
        if (rule == EnterKeyRule.Default) rule = completion.Rules.DefaultEnterKeyRule;
        if (rule == EnterKeyRule.AfterFullyTypedWord)
        {
            var start = Math.Clamp(item.Span.Start, 0, completion.Position);
            return string.Equals(completion.Source.Text[start..completion.Position], item.FilterText, StringComparison.OrdinalIgnoreCase);
        }
        return rule == EnterKeyRule.Always;
    }
    private void UpdateSemanticNavigation(ScriptSourceDocument document)
    {
        if (!csharp) return;
        if (InputActive && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.RightBracket, false)
            && braceVersion == document.Version)
        {
            foreach (var pair in bracePairs)
            {
                if (pair.Left.Contains(caretPosition)) { NavigatePosition(pair.Right.End, document.EditorText); break; }
                if (pair.Right.Contains(caretPosition) || pair.Right.End == caretPosition) { NavigatePosition(pair.Left.Start, document.EditorText); break; }
            }
        }
        if (assistRequest != null) return;
        if (InputActive && (braceRequestedVersion != document.Version || braceRequestedPosition != caretPosition))
        {
            braceRequestedVersion = document.Version; braceRequestedPosition = caretPosition;
            assistRequest = new(ScriptAssistKind.Braces, caretPosition, document.Version); return;
        }
        if (completion != null && (describedCompletion != selectedCompletion || completionDescription.Length == 0))
        {
            if (describedCompletion == selectedCompletion) return;
            describedCompletion = selectedCompletion; completionDescription = "";
            assistRequest = new(ScriptAssistKind.CompletionDescription, caretPosition, document.Version, completion, selectedCompletion); return;
        }
        if (completion != null || signatures.Length != 0 || hoveredSourcePosition < 0 || Environment.TickCount64 - hoverChangedAt < 400) return;
        if (quickInfoRequestedPosition == hoveredSourcePosition && quickInfoVersion == document.Version) return;
        quickInfoRequestedPosition = hoveredSourcePosition; quickInfoVersion = document.Version;
        assistRequest = new(ScriptAssistKind.QuickInfo, hoveredSourcePosition, document.Version);
    }
    private void DrawSemanticNavigation(ImGuiWindowPtr child, Vector2 origin, long sourceVersion)
    {
        var clip = child.InnerClipRect; var mouse = Gui.GetMousePos();
        var hovered = Gui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && mouse.X >= clip.Min.X && mouse.X < clip.Max.X && mouse.Y >= clip.Min.Y && mouse.Y < clip.Max.Y;
        var position = -1; var height = nativeSourceHeight;
        if (hovered)
        {
            var row = (int)MathF.Floor((mouse.Y - origin.Y) / height);
            if (row >= 0 && row < lines.Length)
            {
                var line = lines[row]; var low = SourceElementHit(row, mouse.X - origin.X);
                if (low < line.Length && mouse.X >= origin.X)
                {
                    if (low > 0 && char.IsLowSurrogate(line[low])) low--;
                    position = lineStarts[row] + low;
                }
            }
        }
        if (position != hoveredSourcePosition || hoverSourceVersion != sourceVersion)
        { hoveredSourcePosition = position; hoverSourceVersion = sourceVersion; hoverChangedAt = Environment.TickCount64; quickInfoText = ""; }
        if (braceVersion == sourceVersion)
            foreach (var pair in bracePairs)
                foreach (var span in new[] { pair.Left, pair.Right })
                {
                    var line = Array.BinarySearch(lineStarts, span.Start); if (line < 0) line = ~line - 1;
                    if (line < 0 || line >= lines.Length) continue;
                    var start = span.Start - lineStarts[line]; var end = Math.Min(start + span.Length, lines[line].Length);
                    if (start < 0 || end < start) continue;
                    foreach (var range in SourceRangeRectangles(line, start, end))
                    {
                        var min = origin + new Vector2(range.Minimum.X, line * height);
                        var max = origin + new Vector2(range.Maximum.X, (line + 1) * height);
                        child.DrawList.AddRectFilled(min, max, 0x504080C0u); child.DrawList.AddRect(min, max, 0xFF4080C0u);
                    }
                }
    }
    private void DrawQuickInfo()
    {
        if (quickInfoText.Length == 0 || hoveredSourcePosition < 0 || Environment.TickCount64 - hoverChangedAt < 400) return;
        Gui.BeginTooltip(); Gui.SetNextItemWidth(500 * Gui.GetStyle().FontScaleDpi);
        semanticInfoText.Draw("##SourceQuickInfo", quickInfoText, true, new Vector2(500 * Gui.GetStyle().FontScaleDpi, 0), expandHeightToContent: true);
        Gui.EndTooltip();
    }
}
