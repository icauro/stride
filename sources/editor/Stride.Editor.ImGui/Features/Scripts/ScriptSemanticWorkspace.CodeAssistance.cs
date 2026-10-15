using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.QuickInfo;

namespace Stride.Editor.ImGui;

internal enum ScriptAssistKind { Completion, Signature, Definition, CommitCompletion, CodeActions, DiscoverCodeActions, ApplyCodeAction, QuickInfo, Braces, CompletionDescription }
internal sealed record ScriptAssistRequest(ScriptAssistKind Kind, int Position, long Version, ScriptCompletionSet? Completion = null, int Item = -1, long Generation = 0, ScriptCodeActionItem? Action = null, TextSpan? Selection = null, char? CommitCharacter = null);
internal sealed record ScriptCompletionSet(Document Document, ScriptSourceSnapshot Source, CompletionItem[] Items, int Position, long Epoch, CompletionRules Rules, bool SuggestionMode);
internal sealed record ScriptSignaturePart(string Text, bool Active);
internal sealed record ScriptSignature(ScriptSignaturePart[] Parts);
internal sealed record ScriptDefinition(string Path, int Line, int Column);
internal sealed record ScriptAssistResult(ScriptAssistRequest Request, ScriptSourceSnapshot Source, ScriptCompletionSet? Completion = null,
    ScriptSignature[]? Signatures = null, ScriptDefinition[]? Definitions = null, TextChange[]? Changes = null, int? NewPosition = null,
    ScriptCodeActionItem[]? Actions = null, ScriptDiagnosticReport[]? Failures = null, ScriptCodeActionBatch? CodeActionBatch = null,
    string? QuickInfo = null, ScriptBracePair[]? Braces = null, string? Description = null);

internal sealed partial class ScriptSemanticWorkspace
{
    public async Task<ScriptAssistResult> Assist(ScriptSourceSnapshot[] snapshots, string targetPath, ScriptAssistRequest request, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitForQuiet(token).ConfigureAwait(false);
            var expectedEpoch = Epoch;
            var (document, source) = await PrepareDocument(snapshots, targetPath, expectedEpoch, token).ConfigureAwait(false);
            var position = Math.Clamp(request.Position, 0, source.Text.Length);
            ScriptAssistResult result;
            if (request.Kind == ScriptAssistKind.Completion)
            {
                var service = CompletionService.GetService(document) ?? throw new InvalidOperationException("The C# project has no completion provider.");
                var list = await service.GetCompletionsAsync(document, position, cancellationToken: token).ConfigureAwait(false);
                var prefix = list == null ? "" : source.Text[Math.Min(list.Span.Start, position)..position];
                var items = list == null ? [] : FilterCompletions(service, document, list.ItemsList, prefix);
                result = new(request, source, new ScriptCompletionSet(document, source, items, position, expectedEpoch, list?.Rules ?? service.GetRules(), list?.SuggestionModeItem != null));
            }
            else if (request.Kind == ScriptAssistKind.CommitCompletion)
            {
                var completion = request.Completion ?? throw new InvalidOperationException("The completion request expired.");
                var expectedText = request.CommitCharacter is { } typed ? completion.Source.Text.Insert(completion.Position, typed.ToString()) : completion.Source.Text;
                if (completion.Epoch != expectedEpoch || expectedText != source.Text || request.Item < 0 || request.Item >= completion.Items.Length)
                    throw new OperationCanceledException("The completion source changed.", token);
                var service = CompletionService.GetService(completion.Document) ?? throw new InvalidOperationException("The C# project has no completion provider.");
                var change = await service.GetChangeAsync(completion.Document, completion.Items[request.Item], request.CommitCharacter, token).ConfigureAwait(false);
                TextChange[] changes = change.TextChanges.IsDefaultOrEmpty ? [change.TextChange] : change.TextChanges.ToArray();
                var nextPosition = change.NewPosition ?? change.TextChange.Span.Start + (change.TextChange.NewText?.Length ?? 0);
                if (request.CommitCharacter is { } character && !change.IncludesCommitCharacter)
                {
                    var before = SourceText.From(completion.Source.Text); var after = before.WithChanges(changes);
                    after = after.WithChanges(new TextChange(new TextSpan(nextPosition, 0), character.ToString()));
                    changes = after.GetTextChanges(before).ToArray(); nextPosition++;
                }
                if (request.CommitCharacter != null)
                    changes = SourceText.From(completion.Source.Text).WithChanges(changes).GetTextChanges(SourceText.From(source.Text)).ToArray();
                result = new(request, source, Changes: changes, NewPosition: nextPosition);
            }
            else if (request.Kind is ScriptAssistKind.CodeActions or ScriptAssistKind.DiscoverCodeActions)
            {
                var selection = request.Selection ?? new TextSpan(position, 0);
                if (selection.Start < 0 || selection.End > source.Text.Length) throw new OperationCanceledException("The code-action selection changed.", token);
                var (actions, failures) = await CodeActions(document, source, selection, expectedEpoch, snapshots, token).ConfigureAwait(false);
                result = new(request, source, Actions: actions, Failures: failures);
            }
            else if (request.Kind == ScriptAssistKind.ApplyCodeAction)
            {
                var action = request.Action ?? throw new InvalidOperationException("The code action expired.");
                if (action.Epoch != expectedEpoch || action.Source.Version != source.Version || action.Source.Text != source.Text || action.Selection != request.Selection)
                    throw new OperationCanceledException("The code-action source changed.", token);
                var changes = await CodeActionChanges(action, snapshots, token).ConfigureAwait(false);
                result = new(request, source, NewPosition: position, CodeActionBatch: changes, Failures: changes.Failures);
            }
            else if (request.Kind == ScriptAssistKind.Definition)
            {
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, token).ConfigureAwait(false);
                if (symbol is IAliasSymbol alias) symbol = alias.Target;
                if (symbol != null) symbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, document.Project.Solution, token).ConfigureAwait(false) ?? symbol;
                var locations = (symbol?.Locations ?? []).Where(location => location.IsInSource).Select(location =>
                    (Span: location.GetLineSpan(), Document: location.SourceTree is { } tree ? document.Project.Solution.GetDocument(tree) : null))
                    .Select(location => (location.Span, Path: location.Document == null ? location.Span.Path : AuthoredSourcePath(location.Document)))
                    .Where(location => !string.IsNullOrEmpty(location.Path)).Select(location => new ScriptDefinition(Path.GetFullPath(location.Path!), location.Span.StartLinePosition.Line + 1, location.Span.StartLinePosition.Character + 1))
                    .Distinct().ToArray();
                if (locations.Length == 0 && symbol != null) locations = await MetadataDefinition(document, symbol, token).ConfigureAwait(false);
                result = new(request, source, Definitions: locations);
            }
            else if (request.Kind == ScriptAssistKind.QuickInfo)
            {
                var info = await QuickInfoService.GetService(document)!.GetQuickInfoAsync(document, position, token).ConfigureAwait(false);
                result = new(request, source, QuickInfo: info == null ? "" : string.Join(Environment.NewLine,
                    info.Sections.Select(section => string.Concat(section.TaggedParts.Select(part => part.Text)))));
            }
            else if (request.Kind == ScriptAssistKind.CompletionDescription)
            {
                var completion = request.Completion ?? throw new OperationCanceledException("The completion source changed.", token);
                if (completion.Epoch != expectedEpoch || completion.Source.Text != source.Text || request.Item < 0 || request.Item >= completion.Items.Length)
                    throw new OperationCanceledException("The completion source changed.", token);
                var description = await CompletionService.GetService(completion.Document)!.GetDescriptionAsync(completion.Document, completion.Items[request.Item], token).ConfigureAwait(false);
                result = new(request, source, Description: description.Text);
            }
            else if (request.Kind == ScriptAssistKind.Braces) result = new(request, source, Braces: await MatchingBraces(document, position, token).ConfigureAwait(false));
            else result = new(request, source, Signatures: await NativeSignatures(document, position, token).ConfigureAwait(false));
            if (request.Kind is not (ScriptAssistKind.QuickInfo or ScriptAssistKind.Braces or ScriptAssistKind.CommitCompletion or ScriptAssistKind.ApplyCodeAction))
                result = result with { Braces = await MatchingBraces(document, position, token).ConfigureAwait(false) };
            if (token.IsCancellationRequested) { result.CodeActionBatch?.Dispose(); token.ThrowIfCancellationRequested(); }
            if (Epoch != expectedEpoch) { result.CodeActionBatch?.Dispose(); throw new OperationCanceledException("The project source context changed.", token); }
            return result;
        }
        finally { gate.Release(); }
    }

    private static async Task<ScriptSignature[]> Signatures(Document document, int position, CancellationToken cancellation)
    {
        var root = await document.GetSyntaxRootAsync(cancellation).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellation).ConfigureAwait(false);
        if (root == null || model == null || root.FullSpan.Length == 0) return [];
        var arguments = root.FindToken(Math.Clamp(position - 1, 0, root.FullSpan.End - 1)).Parent?.AncestorsAndSelf()
            .OfType<BaseArgumentListSyntax>().FirstOrDefault(list => list.Span.Start < position && (list.Span.End >= position || list.ContainsDiagnostics)
                && (list is not ArgumentListSyntax parentheses || parentheses.CloseParenToken.IsMissing || position <= parentheses.CloseParenToken.SpanStart));
        if (arguments == null) return [];
        var index = arguments.Arguments.GetSeparators().Count(separator => separator.Span.End <= position);
        var methods = new List<IMethodSymbol>();
        var bound = arguments.Parent == null ? null : model.GetSymbolInfo(arguments.Parent, cancellation).Symbol as IMethodSymbol;
        if (bound != null) methods.Add(bound);
        if (arguments.Parent is InvocationExpressionSyntax invocation)
            methods.AddRange(model.GetMemberGroup(invocation.Expression, cancellation).OfType<IMethodSymbol>());
        else if (arguments.Parent is ObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation, cancellation).Type is INamedTypeSymbol type)
            methods.AddRange(type.InstanceConstructors.Where(method => model.IsAccessible(position, method)));
        return methods.Distinct(SymbolEqualityComparer.Default).OfType<IMethodSymbol>().Select(method =>
        {
            var active = arguments.Arguments.ElementAtOrDefault(index)?.NameColon?.Name.Identifier.ValueText;
            var parameter = active == null ? Math.Min(index, method.Parameters.Length - 1) : method.Parameters.IndexOf(method.Parameters.FirstOrDefault(candidate => candidate.Name == active)!);
            return new ScriptSignature(method.ToDisplayParts(SymbolDisplayFormat.MinimallyQualifiedFormat)
                .Select(part => new ScriptSignaturePart(part.ToString(), part.Symbol is IParameterSymbol selected && selected.Ordinal == parameter)).ToArray());
        }).ToArray();
    }
}
