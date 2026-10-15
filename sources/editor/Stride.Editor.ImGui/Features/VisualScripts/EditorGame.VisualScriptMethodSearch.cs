using Microsoft.CodeAnalysis;
using Stride.Assets.Scripts;
using Stride.Core.Presentation.Extensions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawVisualScriptMethodSearch(VisualScriptDocument document, Method method, VisualScriptCanvasState state)
    {
        var compilation = GetVisualScriptCompilation(document);
        if (!BeginNativeMenu(nameof(MethodCallBlock), CanMutateDocumentPresentation(document) && document.CanSave && compilation != null)) return;
        Gui.SetNextItemWidth(UiScale(320)); Gui.InputText("##SymbolSearch", ref state.MethodSearch, 4096);
        if (state.RequestedMethodSearch != state.MethodSearch || !ReferenceEquals(state.MethodSearchCompilation, compilation))
        {
            StopVisualScriptMethodSearch(state);
            state.RequestedMethodSearch = state.MethodSearch; state.MethodSearchCompilation = compilation;
            state.MethodSearchCancellation = new(); var token = state.MethodSearchCancellation.Token;
            var query = state.MethodSearch;
            state.MethodSearchTask = Task.Run(() => SearchVisualScriptMethods(compilation!, query, token), token);
        }
        if (state.MethodSearchTask?.IsCompleted == true)
        {
            var task = state.MethodSearchTask; state.MethodSearchTask = null;
            state.MethodSearchCancellation?.Dispose(); state.MethodSearchCancellation = null;
            try { state.MethodSearchResults = task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "VisualScript", error.ToString()); }
        }
        foreach (var symbol in state.MethodSearchResults)
            if (DrawNativeMenuItem(symbol.ToDisplayString())) TryVisualScriptPresentationCommand(document, method, () =>
            {
                if (!ReferenceEquals(compilation, GetVisualScriptCompilation(document))) throw new InvalidOperationException("The visual-script compiler context changed.");
                document.AddBlock(method, CreateVisualScriptMethodBlock(symbol), new(
                    (int)MathF.Round(state.PopupPosition.X / 16) * 16, (int)MathF.Round(state.PopupPosition.Y / 16) * 16), compilation);
            });
        Gui.EndMenu();
    }

    private static void StopVisualScriptMethodSearch(VisualScriptCanvasState state)
    {
        state.MethodSearchCancellation?.Cancel();
        DetachBuildTask(state.MethodSearchTask, state.MethodSearchCancellation);
        state.MethodSearchTask = null; state.MethodSearchCancellation = null; state.MethodSearchResults = [];
        state.MethodSearchCompilation = null;
    }

    // Same symbol traversal, priority and result bound as the WPF symbol picker.
    private static IMethodSymbol[] SearchVisualScriptMethods(Compilation compilation, string query, CancellationToken token)
    {
        var seen = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default) { compilation.Assembly };
        var assemblies = new Stack<IAssemblySymbol>(); assemblies.Push(compilation.Assembly);
        while (assemblies.TryPop(out var assembly))
        {
            token.ThrowIfCancellationRequested();
            foreach (var module in assembly.Modules)
                foreach (var reference in module.ReferencedAssemblySymbols) if (seen.Add(reference)) assemblies.Push(reference);
        }
        var results = new List<IMethodSymbol>();
        foreach (var assembly in seen.OrderBy(assembly => SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly) ? 0
            : assembly.Name.Contains("Stride", StringComparison.Ordinal) ? 1
            : assembly.Name == "mscorlib" || assembly.Name.StartsWith("System", StringComparison.Ordinal) ? 3 : 2))
        {
            var members = new Stack<INamespaceOrTypeSymbol>(); members.Push(assembly.GlobalNamespace);
            while (members.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (current is INamespaceSymbol space)
                { foreach (var member in space.GetMembers()) members.Push(member); continue; }
                var type = (INamedTypeSymbol)current;
                foreach (var nested in type.GetTypeMembers()) members.Push(nested);
                var local = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly);
                if (!local && type.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Private) continue;
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    token.ThrowIfCancellationRequested();
                    if (!local && method.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Private) continue;
                    if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator or MethodKind.Conversion)) continue;
                    if (query.Length != 0 && !query.MatchCamelCase(method.ToDisplayString())) continue;
                    results.Add(method); if (results.Count == 100) return results.ToArray();
                }
            }
        }
        return results.ToArray();
    }

    private static MethodCallBlock CreateVisualScriptMethodBlock(IMethodSymbol symbol)
    {
        var unqualified = new SymbolDisplayFormat();
        var qualified = new SymbolDisplayFormat(SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
            SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces, memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);
        var block = new MethodCallBlock { IsMemberCall = !symbol.IsStatic || symbol.IsExtensionMethod };
        block.MethodName = symbol.ToDisplayString(block.IsMemberCall ? unqualified : qualified);
        block.GenerateSlots(block.Slots, new SlotGeneratorContext());
        if (!symbol.IsStatic) block.Slots.Add(new Slot(SlotDirection.Input, SlotKind.Value, "this", type: symbol.ContainingType.ToDisplayString(qualified)));
        foreach (var parameter in symbol.Parameters)
            block.Slots.Add(new Slot(SlotDirection.Input, SlotKind.Value, parameter.Name, type: parameter.Type.ToDisplayString(qualified)));
        if (!symbol.ReturnsVoid) block.Slots.Add(new Slot(SlotDirection.Output, SlotKind.Value, type: symbol.ReturnType.ToDisplayString(qualified)));
        return block;
    }
}
