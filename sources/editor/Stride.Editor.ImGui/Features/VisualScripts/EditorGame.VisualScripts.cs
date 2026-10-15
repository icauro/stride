using Hexa.NET.ImGui;
using Stride.Assets.Scripts;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<VisualScriptDocument, VisualScriptViewState> visualScriptViews = [];
    private VisualScriptBlockTemplate[]? visualScriptTemplates;
    private HashSet<System.Reflection.Assembly>? visualScriptTemplateModules;
    private VisualScriptBlockTemplate[] GetVisualScriptBlockTemplates()
    {
        var actual = Stride.Core.Reflection.AssemblyRegistry.FindAll().Append(typeof(Block).Assembly).ToHashSet();
        if (visualScriptTemplates == null || visualScriptTemplateModules == null || !actual.SetEquals(visualScriptTemplateModules))
        { visualScriptTemplates = VisualScriptBlockTemplate.Discover(); visualScriptTemplateModules = actual; }
        return visualScriptTemplates;
    }
    private void ReleaseVisualScriptView(VisualScriptDocument document)
    {
        if (visualScriptViews.Remove(document, out var view)) foreach (var canvas in view.Methods.Values) StopVisualScriptMethodSearch(canvas);
    }
    private void ClearVisualScriptViews()
    {
        StopVisualScriptCompilation();
        foreach (var view in visualScriptViews.Values) foreach (var canvas in view.Methods.Values) StopVisualScriptMethodSearch(canvas);
        visualScriptViews.Clear(); visualScriptTemplates = null; visualScriptTemplateModules = null;
    }

    private void DrawVisualScriptDocument(VisualScriptDocument document)
    {
        InitializeVisualScriptDocument(document);
        document.InspectorCompilation = GetVisualScriptCompilation(document);
        if (!visualScriptViews.TryGetValue(document, out var view)) visualScriptViews.Add(document, view = new());
        Gui.PushID(document.AuthoredAsset.Id.ToString());
        if (Gui.BeginChild("Visual script members", new Vector2(270, 0), ImGuiChildFlags.Borders))
        {
            var origin = Gui.GetCursorScreenPos();
            RegisterVisualCaptureRegion("visual-scripts.members", "section", "Visual script members", origin, origin + Gui.GetContentRegionAvail(), "visual-scripts.editor");
            DrawVisualScriptMembers(document, view);
        }
        ImGuiScopes.EndChild(); Gui.SameLine();
        if (Gui.BeginChild("Visual script graph", new Vector2(0, 0), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (document.SelectedMethod is { } method)
            {
                if (!view.Methods.TryGetValue(method.Id, out var canvas)) view.Methods.Add(method.Id, canvas = new());
                DrawVisualScriptCanvas(document, method, canvas);
            }
        }
        ImGuiScopes.EndChild(); Gui.PopID();
    }

    private void DrawVisualScriptMembers(VisualScriptDocument document, VisualScriptViewState view)
    {
        var classStart = Gui.GetCursorScreenPos(); var memberWidth = Gui.GetContentRegionAvail().X;
        DrawVisualMember(document, view, document.Asset, nameof(VisualScriptAsset.BaseType), "Base type...");
        DrawVisualMember(document, view, document.Asset, nameof(VisualScriptAsset.Accessibility), "Accessibility");
        DrawVisualMember(document, view, document.Asset, nameof(VisualScriptAsset.IsStatic), "Static");
        RegisterVisualCaptureRegion("visual-scripts.class", "section", "Class", classStart,
            new Vector2(classStart.X + memberWidth, Gui.GetCursorScreenPos().Y), "visual-scripts.members");
        Gui.Separator(); var variablesStart = Gui.GetCursorScreenPos(); Gui.TextUnformatted(EditorLocalization.Text("Variables:")); Gui.SameLine();
        Gui.BeginDisabled(!document.CanSave);
        var addPropertyPressed = Gui.SmallButton("+##Variable");
        RecordVisualScriptHistoryAddPropertyUi(document, addPropertyPressed);
        if (addPropertyPressed) Execute(() => document.AddProperty());
        Controls.Record("visual-script/add-property");
        Gui.EndDisabled();
        foreach (var property in document.Asset.Properties.ToArray())
        {
            Gui.PushID(property.Id.ToString());
            DrawVisualMember(document, view, property, nameof(Property.Type), "Type");
            DrawVisualMember(document, view, property, nameof(Property.Name), "Name");
            DrawVisualSymbolDragSource(document, property, Guid.Empty);
            DrawVisualMember(document, view, property, nameof(Property.Accessibility), "Accessibility");
            DrawVisualMember(document, view, property, nameof(Property.IsStatic), "Static");
            Gui.BeginDisabled(!document.CanSave);
            if (Gui.SmallButton(EditorLocalization.Widget("Delete", "Button"))) Execute(() => document.RemoveProperties([property.Id]));
            Gui.SameLine();
            if (Gui.SmallButton("Up")) Execute(() => document.MoveProperty(property, document.Asset.Properties.IndexOf(property) - 1));
            Gui.SameLine();
            if (Gui.SmallButton("Down")) Execute(() => document.MoveProperty(property, document.Asset.Properties.IndexOf(property) + 1));
            if (document.SelectedMethod is { } selected)
            {
                if (Gui.SmallButton(EditorLocalization.Widget("Get", "Menu"))) Execute(() => document.AddBlock(selected,
                    new VariableGet { Name = property.Name }, new(0, 0), GetVisualScriptCompilation(document)));
                Gui.SameLine();
                if (Gui.SmallButton(EditorLocalization.Widget("Set"))) Execute(() => document.AddBlock(selected,
                    new VariableSet { Name = property.Name }, new(0, 0), GetVisualScriptCompilation(document)));
            }
            Gui.EndDisabled(); Gui.Separator(); Gui.PopID();
        }
        RegisterVisualCaptureRegion("visual-scripts.variables", "section", "Variables", variablesStart,
            new Vector2(variablesStart.X + memberWidth, Gui.GetCursorScreenPos().Y), "visual-scripts.members");
        var functionsStart = Gui.GetCursorScreenPos(); Gui.TextUnformatted(EditorLocalization.Text("Functions:")); Gui.SameLine(); Gui.BeginDisabled(!document.CanSave);
        if (Gui.SmallButton("+##Function")) Execute(() => document.AddMethod());
        Gui.EndDisabled();
        if (view.Semantics is { } semantics && semantics.Overrides.Length != 0 && BeginNativeMenu("Override", document.CanSave))
        {
            foreach (var symbol in semantics.Overrides)
                if (DrawNativeMenuItem(symbol.ToDisplayString())) Execute(() => document.AddMethod(symbol));
            Gui.EndMenu();
        }
        foreach (var function in document.Asset.Methods.ToArray())
        {
            Gui.PushID(function.Id.ToString());
            if (Gui.Selectable(function.Name ?? "", document.SelectedMethodId == function.Id)) document.SelectedMethodId = function.Id;
            if (Gui.BeginPopupContextItem("Function menu"))
            {
                if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "", false, document.CanSave)) Execute(() => document.RemoveMethod(function));
                Gui.EndPopup();
            }
            Gui.PopID();
        }
        RegisterVisualCaptureRegion("visual-scripts.functions", "section", "Functions", functionsStart,
            new Vector2(functionsStart.X + memberWidth, Gui.GetCursorScreenPos().Y), "visual-scripts.members");
        if (document.SelectedMethod is not { } method) { document.SelectInspectorBlocks([]); return; }
        Gui.Separator(); Gui.PushID(method.Id.ToString());
        var functionStart = Gui.GetCursorScreenPos();
        DrawVisualMember(document, view, method, nameof(Method.Name), "Name");
        DrawVisualMember(document, view, method, nameof(Method.ReturnType), "Return type...");
        DrawVisualMember(document, view, method, nameof(Method.Accessibility), "Accessibility");
        DrawVisualMember(document, view, method, nameof(Method.VirtualModifier), "Virtual modifier");
        DrawVisualMember(document, view, method, nameof(Method.IsStatic), "Static");
        RegisterVisualCaptureRegion("visual-scripts.function", "section", "Function", functionStart,
            new Vector2(functionStart.X + memberWidth, Gui.GetCursorScreenPos().Y), "visual-scripts.members");
        var parametersStart = Gui.GetCursorScreenPos(); Gui.TextUnformatted(EditorLocalization.Text("Function parameters:")); Gui.SameLine(); Gui.BeginDisabled(!document.CanSave);
        if (Gui.SmallButton("+##Parameter")) Execute(() => document.AddParameter(method)); Gui.EndDisabled();
        foreach (var parameter in method.Parameters.ToArray())
        {
            Gui.PushID(parameter.Id.ToString());
            DrawVisualMember(document, view, parameter, nameof(Parameter.RefKind), "Ref kind");
            DrawVisualMember(document, view, parameter, nameof(Parameter.Type), "Type");
            DrawVisualMember(document, view, parameter, nameof(Parameter.Name), "Name");
            DrawVisualSymbolDragSource(document, parameter, method.Id);
            Gui.BeginDisabled(!document.CanSave);
            if (Gui.SmallButton(EditorLocalization.Widget("Delete", "Button"))) Execute(() => document.RemoveParameters(method, [parameter.Id]));
            Gui.EndDisabled(); Gui.PopID();
        }
        RegisterVisualCaptureRegion("visual-scripts.parameters", "section", "Function parameters", parametersStart,
            new Vector2(parametersStart.X + memberWidth, Gui.GetCursorScreenPos().Y), "visual-scripts.members");
        Gui.PopID();
        var selectedBlocks = view.Methods.TryGetValue(method.Id, out var canvas)
            ? method.Blocks.Values.Where(block => canvas.SelectedBlocks.Contains(block.Id)).ToArray() : [];
        document.SelectInspectorBlocks(selectedBlocks);
        foreach (var block in selectedBlocks)
            {
                Gui.Separator(); var blockStart = Gui.GetCursorScreenPos(); Gui.TextUnformatted(block.Title ?? block.GetType().Name); Gui.PushID(block.Id.ToString());
                document.SelectInspectorBlock(block);
                var complexFields = document.GetVisualScriptBlockFields(block);
                Gui.BeginDisabled(!document.CanSave);
                BeginAssetPresenterRows(document);
                DrawGameSettingFields(document, complexFields);
                DrawExtensionVirtualProperties(document);
                Gui.EndDisabled();
                var collectionAction = pendingGameSettingCollectionAction; pendingGameSettingCollectionAction = null;
                if (collectionAction != null) Execute(collectionAction);
                foreach (var slot in block.Slots.Where(slot => slot.Kind == SlotKind.Value && slot.Direction == SlotDirection.Input))
                {
                    Gui.PushID(slot.Id.ToString()); DrawVisualMember(document, view, slot, nameof(Slot.Value), slot.Name ?? "Value"); Gui.PopID();
                }
                RegisterVisualCaptureRegion("visual-scripts.block-editor." + block.Id, "section", block.Title ?? block.GetType().Name,
                    blockStart, new Vector2(blockStart.X + memberWidth, Gui.GetCursorScreenPos().Y), "visual-scripts.members");
                Gui.PopID();
            }
        if (view.Semantics is { Issues.Length: > 0 } diagnostics)
        {
            Gui.Separator();
            for (var index = 0; index < diagnostics.Issues.Length; index++)
            {
                var issue = diagnostics.Issues[index]; Gui.PushID(index);
                Gui.PushStyleColor(ImGuiCol.Text, issue.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? 0xFF7777FFu : 0xFF77CCFFu);
                if (Gui.Selectable(issue.Message))
                {
                    var owningMethod = document.Asset.Methods.FirstOrDefault(candidate =>
                        issue.Block is { } blockId && candidate.Blocks.Values.Any(block => block.Id == blockId)
                        || issue.Link is { } linkId && candidate.Links.Values.Any(link => link.Id == linkId));
                    if (owningMethod != null)
                    {
                        document.SelectedMethodId = owningMethod.Id;
                        if (!view.Methods.TryGetValue(owningMethod.Id, out var target)) view.Methods.Add(owningMethod.Id, target = new());
                        target.SelectedBlocks.Clear(); target.SelectedLinks.Clear();
                        if (issue.Block is { } id)
                        {
                            target.SelectedBlocks.Add(id);
                            var block = owningMethod.Blocks.Values.First(candidate => candidate.Id == id);
                            target.Pan = new Vector2(60, 60) - new Vector2(block.Position.X, block.Position.Y) * target.Zoom;
                        }
                        if (issue.Link is { } link) target.SelectedLinks.Add(link);
                    }
                    else if (!string.IsNullOrEmpty(issue.Path))
                        Execute(() => NavigateScriptDiagnostic(new BuildDiagnostic(issue.Path, issue.Line, issue.Column,
                            issue.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? Stride.Core.Diagnostics.LogMessageType.Error
                                : Stride.Core.Diagnostics.LogMessageType.Warning, issue.Code, issue.Message)));
                }
                Gui.PopStyleColor(); Gui.PopID();
            }
        }
    }

    private void DrawVisualMember(VisualScriptDocument document, VisualScriptViewState view, object owner, string member, string label)
    {
        var node = document.ObjectNode(owner).TryGetChild(member);
        if (node == null) return;
        var value = node.Retrieve(); var type = node.Type;
        Gui.BeginDisabled(!document.CanEditMember(owner, member)); Gui.SetNextItemWidth(150);
        if (type == typeof(bool))
        {
            var next = value is true;
            if (Gui.Checkbox(label, ref next)) Execute(() => document.SetMember(owner, member, next));
        }
        else if (type.IsEnum)
        {
            var open = Gui.BeginCombo(label, value?.ToString() ?? "");
            var identity = owner is Stride.Core.IIdentifiable identifiable ? identifiable.Id.ToString() : document.Asset.Id.ToString();
            RegisterVisualCaptureItem("visual-scripts.field." + identity + "." + member, "control", label);
            if (open)
            {
                foreach (var next in Enum.GetValues(type))
                    if (Gui.Selectable(next.ToString()!, Equals(next, value))) Execute(() => document.SetMember(owner, member, next));
                Gui.EndCombo();
            }
        }
        else if (type == typeof(string))
        {
            var draft = document.GetTextDraft(owner, member);
            if (owner.GetType().GetProperty(member)?.IsDefined(typeof(ScriptCodeAttribute), true) == true)
                Gui.InputTextMultiline(label, ref draft.Text, 65536, new Vector2(235, 120));
            else Gui.InputText(label, ref draft.Text, 65536);
            draft.Active = Gui.IsItemActive();
            if (Gui.IsItemDeactivatedAfterEdit()) Execute(() => document.CommitEdits());
        }
        if (type == typeof(string) || type == typeof(bool))
        {
            var identity = owner is Stride.Core.IIdentifiable identifiable ? identifiable.Id.ToString() : document.Asset.Id.ToString();
            RegisterVisualCaptureItem("visual-scripts.field." + identity + "." + member, "control", label);
        }
        Gui.EndDisabled();
    }

    private sealed class VisualScriptViewState
    {
        internal readonly Dictionary<Guid, VisualScriptCanvasState> Methods = [];
        internal VisualScriptSemanticResult? Semantics;
    }
    private sealed class VisualScriptCanvasState
    {
        internal long LayoutRevision = -1;
        internal float LayoutFontSize;
        internal Block[] Blocks = [];
        internal Link[] Links = [];
        internal readonly Dictionary<Block, (Vector2 Size, string Title)> Layout = [];
        internal readonly Dictionary<Slot, Vector2> SlotOffsets = [];
        internal Vector2 Pan = new(40, 40);
        internal float Zoom = 1;
        internal readonly HashSet<Guid> SelectedBlocks = [];
        internal readonly HashSet<Guid> SelectedLinks = [];
        internal readonly Dictionary<Guid, Stride.Core.Mathematics.Int2> DragStart = [];
        internal Vector2 DragOffset;
        internal long GestureRevision = -1;
        internal Slot? Connecting;
        internal Slot? ContextSlot;
        internal bool Panning;
        internal Vector2? Marquee;
        internal Vector2 PopupPosition;
        internal string MethodSearch = "", RequestedMethodSearch = "";
        internal Microsoft.CodeAnalysis.Compilation? MethodSearchCompilation;
        internal Task<Microsoft.CodeAnalysis.IMethodSymbol[]>? MethodSearchTask;
        internal CancellationTokenSource? MethodSearchCancellation;
        internal Microsoft.CodeAnalysis.IMethodSymbol[] MethodSearchResults = [];
    }
}
