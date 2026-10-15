using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Assets.Scripts;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string VisualSymbolDragType = "STRIDE_VISUAL_SCRIPT_SYMBOL";
    private readonly record struct VisualSymbolDragPayload(Guid Document, Guid Method, Guid Symbol);
    private sealed class VisualSymbolDraft { internal string Source = "", Text = ""; internal int Selected = -1; }
    private readonly ConditionalWeakTable<IMemberNode, VisualSymbolDraft> visualSymbolDrafts = new();

    private unsafe void DrawVisualSymbolDragSource(VisualScriptDocument document, Symbol symbol, Guid method)
    {
        if (!Gui.BeginDragDropSource()) return;
        try
        {
            var payload = new VisualSymbolDragPayload(document.SymbolDragIdentity, method, symbol.Id);
            Gui.SetDragDropPayload(VisualSymbolDragType, &payload, (nuint)sizeof(VisualSymbolDragPayload));
            Gui.TextUnformatted(symbol.Name ?? "");
        }
        finally { Gui.EndDragDropSource(); }
    }

    private unsafe bool TryDrawVisualScriptSymbolReference(AssetMetadataDocument settings, GameSettingField field, object? value)
    {
        if (settings is not VisualScriptDocument document || field.Type != typeof(string)
            || !field.Node.MemberDescriptor.GetCustomAttributes<ScriptVariableReferenceAttribute>(true).Any()) return false;
        var source = (string?)value ?? "";
        var draft = visualSymbolDrafts.GetValue(field.Node, _ => new() { Source = source, Text = source });
        if (draft.Source != source) { draft.Source = source; draft.Text = source; draft.Selected = -1; }
        var editable = document.CanEditPresentedGameSetting(field) && !IsAuthoringPipelineUpdating;
        var origin = Gui.GetCursorScreenPos();
        var width = Gui.GetContentRegionAvail().X;
        Gui.SetNextItemWidth(Math.Max(1, width - Gui.GetFrameHeight() - Gui.GetStyle().ItemSpacing.X));
        var previousText = draft.Text;
        var entered = Gui.InputText("##Value", ref draft.Text, 16384, ImGuiInputTextFlags.EnterReturnsTrue);
        if (previousText != draft.Text) draft.Selected = -1;
        var activated = Gui.IsItemActivated();
        var deactivated = Gui.IsItemDeactivatedAfterEdit();
        var focused = Gui.IsItemActive();
        DrawSymbolDrop();
        Gui.SameLine();
        var open = Gui.ArrowButton("##Symbols", ImGuiDir.Down);
        if (activated || open) Gui.OpenPopup("##SymbolChoices");
        var choices = visualScriptViews.TryGetValue(document, out var view) && view.Semantics is { } semantics
            && ReferenceEquals(semantics.Compilation, GetVisualScriptCompilation(document))
            ? semantics.VariableReferences.Where(candidate => candidate.Method == document.SelectedMethodId
                && candidate.Name.Contains(draft.Text, StringComparison.OrdinalIgnoreCase)).ToArray() : [];
        Gui.SetNextWindowPos(new System.Numerics.Vector2(origin.X, origin.Y + Gui.GetFrameHeight()));
        Gui.SetNextWindowSize(new System.Numerics.Vector2(width, 0));
        var popup = Gui.BeginPopup("##SymbolChoices", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing);
        if (popup)
        {
            try
            {
                if (focused && Gui.IsKeyPressed(ImGuiKey.DownArrow, false)) draft.Selected = Math.Min(choices.Length - 1, draft.Selected + 1);
                if (focused && Gui.IsKeyPressed(ImGuiKey.UpArrow, false)) draft.Selected = Math.Max(0, draft.Selected - 1);
                for (var index = 0; index < choices.Length; index++)
                {
                    var candidate = choices[index];
                    Gui.PushID(index);
                    try
                    {
                        if (Gui.Selectable(candidate.Kind + " " + candidate.Name + " (" + candidate.Type + ")", index == draft.Selected || candidate.Name == source) && editable)
                        { Apply(candidate.Name); Gui.CloseCurrentPopup(); }
                        if (focused && index == draft.Selected && (Gui.IsKeyPressed(ImGuiKey.DownArrow, false) || Gui.IsKeyPressed(ImGuiKey.UpArrow, false))) Gui.SetScrollHereY();
                    }
                    finally { Gui.PopID(); }
                }
                if (editable && entered && draft.Selected >= 0 && draft.Selected < choices.Length)
                { Apply(choices[draft.Selected].Name); entered = false; Gui.CloseCurrentPopup(); }
                if (focused && Gui.IsKeyPressed(ImGuiKey.Escape, false)) Gui.CloseCurrentPopup();
            }
            finally { Gui.EndPopup(); }
        }
        if (editable && (entered || deactivated) && draft.Text != draft.Source) Apply(draft.Text);
        return true;

        void Apply(string name) => Execute(() =>
        {
            if (!document.CanEditPresentedGameSetting(field)) throw new InvalidOperationException("The visual-script reference is read-only.");
            document.UpdateGameSetting(field, name); document.EndGameSettingEdit();
            draft.Source = draft.Text = name;
        });
        void DrawSymbolDrop()
        {
            if (!editable || !Gui.BeginDragDropTarget()) return;
            try
            {
                var payload = Gui.AcceptDragDropPayload(VisualSymbolDragType, ImGuiDragDropFlags.AcceptBeforeDelivery);
                if (payload.IsNull || payload.DataSize != sizeof(VisualSymbolDragPayload)) return;
                var identity = *(VisualSymbolDragPayload*)payload.Data;
                var owner = sessionHistory?.Registry.PublishedStates.Where(state => !state.IsDisposed)
                    .Select(state => state.Document).OfType<VisualScriptDocument>()
                    .FirstOrDefault(candidate => candidate.SymbolDragIdentity == identity.Document);
                Symbol? symbol = identity.Method == Guid.Empty ? owner?.Asset.Properties.FirstOrDefault(candidate => candidate.Id == identity.Symbol)
                    : owner?.Asset.Methods.FirstOrDefault(candidate => candidate.Id == identity.Method)?.Parameters.FirstOrDefault(candidate => candidate.Id == identity.Symbol);
                if (symbol != null && payload.IsDelivery()) Apply(symbol.Name);
            }
            finally { Gui.EndDragDropTarget(); }
        }
    }
}
