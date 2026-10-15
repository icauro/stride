using System.Collections;
using System.Reflection;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;

namespace Stride.Editor.ImGui;

internal sealed record ScriptBracePair(TextSpan Left, TextSpan Right);

internal sealed partial class ScriptSemanticWorkspace
{
    private NativeAnalyzerLoader? editorAnalyzerLoader;
    private AnalyzerReference[]? editorAnalyzerReferences;
    private readonly Dictionary<string, (Document Document, object Service, Type Contract)> metadataDocuments = new(StringComparer.OrdinalIgnoreCase);
    private void ReleaseMetadataDocuments()
    {
        foreach (var entry in metadataDocuments)
            try { entry.Value.Contract.GetMethod("TryRemoveDocumentFromWorkspace")!.Invoke(entry.Value.Service, [entry.Key]); }
            catch (Exception error) { Console.Error.WriteLine("Metadata source release failed: " + error.Message); }
        metadataDocuments.Clear();
    }
    private Solution WithEditorAnalyzers(Solution solution)
    {
        if (editorAnalyzerReferences == null)
        {
            editorAnalyzerLoader = new NativeAnalyzerLoader();
            editorAnalyzerReferences = new[] { typeof(Compilation).Assembly, typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly,
                    typeof(CompletionService).Assembly, Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features") }
                .Distinct().Select(assembly => (AnalyzerReference)new AnalyzerFileReference(assembly.Location, editorAnalyzerLoader)).ToArray();
        }
        var paths = solution.AnalyzerReferences.Select(reference => reference.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return solution.AddAnalyzerReferences(editorAnalyzerReferences.Where(reference => !paths.Contains(reference.FullPath)));
    }
    private static CompletionItem[] FilterCompletions(CompletionService service, Document document, IReadOnlyList<CompletionItem> items, string query)
    {
        CompletionItem[] matching;
        if (query.Length == 0) matching = items.ToArray();
        else
        {
            var type = typeof(Workspace).Assembly.GetType("Microsoft.CodeAnalysis.PatternMatching.PatternMatcher", true)!;
            var factory = type.GetMethod("CreatePatternMatcher", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            using var matcher = (IDisposable)factory.Invoke(null, [query, CultureInfo.CurrentCulture, false, true])!;
            var extension = typeof(Workspace).Assembly.GetType("Microsoft.CodeAnalysis.PatternMatching.PatternMatcherExtensions", true)!;
            var matches = extension.GetMethod("Matches", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            matching = items.Where(item => (bool)matches.Invoke(null, [matcher, item.FilterText])!).ToArray();
        }
        var best = service.FilterItems(document, matching.ToImmutableArray(), query);
        return matching.OrderBy(item => best.IndexOf(item) is var rank && rank >= 0 ? rank : int.MaxValue)
            .ThenBy(item => item.SortText, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    // These are the actual pinned Roslyn feature contracts used by the WPF
    // wrappers. They are internal, so the bridge invokes the native exports;
    // it does not synthesize metadata, signatures or bracket matches.
    private static Type FeatureType(string name) => typeof(CompletionService).Assembly.GetType(name, throwOnError: true)!;
    private static object NativeDefault(Type type) => type.GetField("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
        ?? type.GetProperty("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
        ?? Activator.CreateInstance(type, nonPublic: true)!;
    private static object? Value(object? target, string name) => target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
        ?? target?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
    private static async Task<object?> InvokeNative(object service, MethodInfo method, object?[] arguments)
    {
        object? pending;
        try { pending = method.Invoke(service, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        if (pending == null) return null;
        var task = pending as Task ?? pending.GetType().GetMethod("AsTask", Type.EmptyTypes)?.Invoke(pending, null) as Task
            ?? throw new InvalidOperationException("The native Roslyn service returned no asynchronous operation.");
        await task.ConfigureAwait(false);
        return Value(task, "Result");
    }
    private async Task<ScriptBracePair[]> MatchingBraces(Document document, int position, CancellationToken token)
    {
        var contract = FeatureType("Microsoft.CodeAnalysis.BraceMatching.IBraceMatchingService");
        var service = codeActionProviders!.GetNativeService(contract); var method = contract.GetMethod("GetMatchingBracesAsync")!;
        var options = NativeDefault(method.GetParameters().Single(parameter => parameter.Name == "options").ParameterType);
        var result = new List<ScriptBracePair>();
        foreach (var at in new[] { Math.Max(0, position - 1), position }.Distinct())
        {
            var pair = await InvokeNative(service, method, [document, at, options, token]).ConfigureAwait(false);
            if (pair != null && Value(pair, "LeftSpan") is TextSpan left && Value(pair, "RightSpan") is TextSpan right)
                result.Add(new(left, right));
        }
        return result.Distinct().ToArray();
    }
    private async Task<ScriptDefinition[]> MetadataDefinition(Document document, ISymbol symbol, CancellationToken token)
    {
        var contract = FeatureType("Microsoft.CodeAnalysis.MetadataAsSource.IMetadataAsSourceFileService");
        var service = codeActionProviders!.GetNativeService(contract);
        if (!(bool)contract.GetMethod("IsNavigableMetadataSymbol")!.Invoke(service, [symbol])!) return [];
        var method = contract.GetMethod("GetGeneratedFileAsync")!;
        var options = NativeDefault(method.GetParameters().Single(parameter => parameter.Name == "options").ParameterType);
        var result = await InvokeNative(service, method, [document.Project.Solution.Workspace, document.Project, symbol, true, options, token]).ConfigureAwait(false);
        if (Value(result, "FilePath") is not string path || Value(result, "IdentifierLocation") is not Location location) return [];
        var metadataPath = Path.GetFullPath(path);
        if (!metadataDocuments.ContainsKey(metadataPath))
        {
            var text = SourceText.From(await File.ReadAllTextAsync(metadataPath, token).ConfigureAwait(false));
            object?[] arguments = [metadataPath, text.Container, null];
            var added = (bool)contract.GetMethod("TryAddDocumentToWorkspace")!.Invoke(service, arguments)!;
            if (added && arguments[2] is DocumentId id && contract.GetMethod("TryGetWorkspace")!.Invoke(service, null) is Workspace metadataWorkspace
                && metadataWorkspace.CurrentSolution.GetDocument(id) is { } metadataDocument)
                metadataDocuments[metadataPath] = (metadataDocument, service, contract);
        }
        var span = location.GetLineSpan();
        return [new(path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1)];
    }
    private async Task<ScriptSignature[]> NativeSignatures(Document document, int position, CancellationToken token)
    {
        var contract = FeatureType("Microsoft.CodeAnalysis.SignatureHelp.SignatureHelpService");
        var service = codeActionProviders!.GetNativeService(contract);
        var method = contract.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetSignatureHelpAsync" && method.GetParameters().Length == 4
                && method.GetParameters()[0].ParameterType == typeof(Document));
        var triggerType = method.GetParameters()[2].ParameterType;
        var constructor = triggerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var reasonType = constructor.GetParameters()[0].ParameterType;
        var trigger = constructor.Invoke([Enum.Parse(reasonType, "InvokeSignatureHelpCommand"), null]);
        var returned = await InvokeNative(service, method, [document, position, trigger, token]).ConfigureAwait(false);
        var native = Value(returned, "Item2") ?? returned;
        if (Value(native, "Items") is not IEnumerable items) return [];
        var index = Value(native, "SemanticParameterIndex") is int actual ? actual : 0;
        var argument = Value(native, "ArgumentName") as string;
        var result = new List<ScriptSignature>();
        foreach (var item in items)
        {
            var parts = new List<ScriptSignaturePart>();
            void Add(object? values, bool active = false)
            { if (values is IEnumerable sequence) foreach (var part in sequence) parts.Add(new(part?.ToString() ?? "", active)); }
            Add(Value(item, "PrefixDisplayParts"));
            var ordinal = 0;
            if (Value(item, "Parameters") is IEnumerable parameters)
                foreach (var parameter in parameters)
                {
                    if (ordinal != 0) Add(Value(item, "SeparatorDisplayParts"));
                    var active = argument == null ? ordinal == index : Equals(Value(parameter, "Name"), argument);
                    Add(Value(parameter, "PrefixDisplayParts"), active); Add(Value(parameter, "DisplayParts"), active);
                    Add(Value(parameter, "SuffixDisplayParts"), active); ordinal++;
                }
            Add(Value(item, "SuffixDisplayParts")); Add(Value(item, "DescriptionParts"));
            result.Add(new(parts.ToArray()));
        }
        return result.ToArray();
    }
}
