using Stride.Core.Assets;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Stride.Editor.ImGui;

internal sealed partial class AuthoredProjectProperties
{
    private static string ExpandStagingFileScope(string value, string source)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildThisFileDirectory"] = Path.GetDirectoryName(source)! + Path.DirectorySeparatorChar,
            ["MSBuildThisFileFullPath"] = source,
            ["MSBuildThisFileName"] = Path.GetFileNameWithoutExtension(source),
            ["MSBuildThisFileExtension"] = Path.GetExtension(source),
            ["MSBuildThisFile"] = Path.GetFileName(source),
        };
        foreach (var field in fields)
            value = value.Replace("$(" + field.Key + ")", EscapeStagingLiteral(field.Value), StringComparison.OrdinalIgnoreCase);
        return value;
    }
    private static byte[] PreserveToolchainFileScope(byte[] bytes, ProjectRootElement owner)
    {
        using var input = new MemoryStream(bytes);
        var xml = System.Xml.Linq.XDocument.Load(input, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildThisFileDirectory"] = Path.GetDirectoryName(owner.FullPath)! + Path.DirectorySeparatorChar,
            ["MSBuildThisFileFullPath"] = owner.FullPath,
            ["MSBuildThisFileName"] = Path.GetFileNameWithoutExtension(owner.FullPath),
            ["MSBuildThisFileExtension"] = Path.GetExtension(owner.FullPath),
            ["MSBuildThisFile"] = Path.GetFileName(owner.FullPath),
        };
        string Scope(string value)
        {
            foreach (var property in values)
                value = value.Replace("$(" + property.Key + ")", EscapeStagingLiteral(property.Value), StringComparison.OrdinalIgnoreCase);
            return value;
        }
        foreach (var element in xml.Descendants())
        {
            foreach (var attribute in element.Attributes()) attribute.Value = Scope(attribute.Value);
            var property = element.Parent?.Name.LocalName == "PropertyGroup";
            var metadata = element.Parent?.Parent?.Name.LocalName is "ItemGroup" or "ItemDefinitionGroup";
            if (property || metadata)
                foreach (var text in element.Nodes().OfType<System.Xml.Linq.XText>()) text.Value = Scope(text.Value);
        }
        using var output = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(output, new() { Encoding = owner.Encoding ?? new System.Text.UTF8Encoding(false), Indent = false })) xml.Save(writer);
        return output.ToArray();
    }
    private static bool HasDefiningScalarBindings(ProjectRootElement owner)
    {
        var xml = System.Xml.Linq.XDocument.Parse(owner.RawXml, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        return xml.Descendants().Where(element => element.Ancestors().Any(parent => parent.Name.LocalName == "Target"))
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value).Concat(
                element.Parent?.Name.LocalName == "PropertyGroup" || element.Parent?.Parent?.Name.LocalName is "ItemGroup" or "ItemDefinitionGroup"
                    ? element.Nodes().OfType<System.Xml.Linq.XText>().Select(text => text.Value) : Enumerable.Empty<string>()))
            .Any(value => StagedFileItemTypes.Append("Reference").Any(type => new[] { "DefiningProjectName", "DefiningProjectExtension", "DefiningProjectFullPath", "DefiningProjectDirectory" }
                .Any(field => value.Contains("%(" + type + "." + field + ")", StringComparison.OrdinalIgnoreCase))));
    }
    private static byte[] RebindDefiningScalarOrigins(byte[] bytes, System.Text.Encoding encoding, string contextProperty, string originalCaller)
    {
        using var input = new MemoryStream(bytes);
        var xml = System.Xml.Linq.XDocument.Load(input, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        string Bind(string value)
        {
            foreach (var type in StagedFileItemTypes.Append("Reference"))
            foreach (var field in new[] { "DefiningProjectName", "DefiningProjectExtension" })
                value = value.Replace("%(" + type + "." + field + ")", "$([MSBuild]::ValueOrDefault('%(" + type + "." + contextProperty + "_" + field + ")', '%(" + type + "." + field + ")'))", StringComparison.OrdinalIgnoreCase);
            return value;
        }
        foreach (var element in xml.Descendants().Where(element => element.Ancestors().Any(parent => parent.Name.LocalName == "Target")))
        {
            foreach (var attribute in element.Attributes())
                attribute.Value = attribute.Name.LocalName == "Condition" ? BindDefiningIdentityComparisons(Bind(attribute.Value), contextProperty, originalCaller) : Bind(attribute.Value);
            var property = element.Parent?.Name.LocalName == "PropertyGroup";
            var metadata = element.Parent?.Parent?.Name.LocalName is "ItemGroup" or "ItemDefinitionGroup";
            if (property || metadata)
                foreach (var text in element.Nodes().OfType<System.Xml.Linq.XText>()) text.Value = Bind(text.Value);
        }
        PrepareNativeImportedLinks(xml, contextProperty);
        using var output = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(output, new() { Encoding = encoding, Indent = false })) xml.Save(writer);
        return output.ToArray();
    }
    private static string BindDefiningIdentityComparisons(string value, string contextProperty, string originalCaller)
    {
        const string token = @"(?:%\((?:ProjectReference|Compile|Content|None|EmbeddedResource|AdditionalFiles|Analyzer|Resource|Page|ApplicationDefinition|NativeReference|Reference)\.DefiningProject(?:FullPath|Directory)\)|\$\(MSBuildProject(?:FullPath|Directory)\))";
        var pattern = "'(?<left>" + token + ")'\\s*(?<op>==|!=)\\s*'(?<right>" + token + ")'";
        return System.Text.RegularExpressions.Regex.Replace(value, pattern, match =>
        {
            var left = match.Groups["left"].Value; var right = match.Groups["right"].Value;
            var leftOrigin = left.StartsWith("%(", StringComparison.Ordinal); var rightOrigin = right.StartsWith("%(", StringComparison.Ordinal);
            if (leftOrigin == rightOrigin) return match.Value;
            var origin = leftOrigin ? left : right; var caller = leftOrigin ? right : left;
            var dot = origin.IndexOf('.'); var type = origin[2..dot]; var field = origin[(dot + 1)..^1];
            var source = "%(" + type + "." + contextProperty + "_" + field + ")";
            var original = EscapeStagingLiteral(caller.Equals("$(MSBuildProjectDirectory)", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(originalCaller)! : originalCaller);
            var sourceLeft = leftOrigin ? source : original; var sourceRight = rightOrigin ? source : original;
            // Captured items use actual source identity. Newly produced items have
            // no capture and retain the unmodified native caller comparison.
            return "(('" + source + "' != '' And '" + sourceLeft + "' " + match.Groups["op"].Value + " '" + sourceRight + "') Or ('" + source + "' == '' And (" + match.Value + ")))";
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
    private sealed record StagedProjectItem(string Type, string Include, IReadOnlyDictionary<string, string> Metadata,
        string DefiningProjectName, string DefiningProjectExtension, string DefiningProjectFullPath, string DefiningProjectDirectory, string SourceFullPath);
    private static readonly string[] StagedFileItemTypes = ["ProjectReference", "Compile", "Content", "None", "EmbeddedResource", "AdditionalFiles", "Analyzer", "Resource", "Page", "ApplicationDefinition", "NativeReference", "_DeploymentManifestIconFile"];
    private static StagedProjectItem[] CaptureStagingItems(Project project) => project.Items
        .Where(item => item.ItemType == "Reference" || StagedFileItemTypes.Contains(item.ItemType, StringComparer.Ordinal))
        .Select(item => new StagedProjectItem(item.ItemType, item.EvaluatedInclude,
            item.Metadata.ToDictionary(metadata => metadata.Name, metadata => metadata.EvaluatedValue, StringComparer.OrdinalIgnoreCase),
            item.GetMetadataValue("DefiningProjectName"), item.GetMetadataValue("DefiningProjectExtension"),
            item.GetMetadataValue("DefiningProjectFullPath"), item.GetMetadataValue("DefiningProjectDirectory"), item.GetMetadataValue("FullPath")))
        .ToArray();
    private static bool SameStagingItems(StagedProjectItem[] left, StagedProjectItem[] right) => left.Length == right.Length
        && left.Zip(right).All(pair => pair.First.Type == pair.Second.Type && pair.First.Include == pair.Second.Include
            && pair.First.DefiningProjectName == pair.Second.DefiningProjectName && pair.First.DefiningProjectExtension == pair.Second.DefiningProjectExtension
            && pair.First.DefiningProjectFullPath == pair.Second.DefiningProjectFullPath && pair.First.DefiningProjectDirectory == pair.Second.DefiningProjectDirectory
            && pair.First.SourceFullPath == pair.Second.SourceFullPath
            && pair.First.Metadata.Count == pair.Second.Metadata.Count
            && pair.First.Metadata.All(value => pair.Second.Metadata.TryGetValue(value.Key, out var other) && value.Value == other));
    private void PublishStagingItems(IReadOnlyDictionary<string, StagedProjectItem[]> contexts, string contextProperty,
        Func<string, string> mapProject, Func<string, string> mapResource)
    {
        foreach (var context in contexts)
        {
            if (context.Key.IndexOfAny(['\'', '\r', '\n']) >= 0)
                throw new InvalidOperationException("The native framework cannot be represented in a staging condition.");
            var group = root.CreateItemGroupElement(); root.AppendChild(group);
            group.Condition = "'$(" + contextProperty + ")' == '" + context.Key + "'";
            foreach (var type in StagedFileItemTypes.Append("Reference"))
            {
                var removal = root.CreateItemElement(type); removal.Remove = "@(" + type + ")"; group.AppendChild(removal);
            }
            foreach (var captured in context.Value)
            {
                var item = root.CreateItemElement(captured.Type);
                var include = captured.Type == "Reference" ? captured.Include
                    : (captured.Type == "ProjectReference" ? mapProject : mapResource)(Path.GetFullPath(captured.Include, Path.GetDirectoryName(projectPath)!));
                item.Include = EscapeStagingLiteral(include);
                foreach (var metadata in captured.Metadata)
                {
                    var value = captured.Type == "Reference" && metadata.Key.Equals("HintPath", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(metadata.Value)
                        ? mapResource(Path.GetFullPath(metadata.Value, Path.GetDirectoryName(projectPath)!)) : metadata.Value;
                    item.AddMetadata(metadata.Key, EscapeStagingLiteral(value));
                }
                item.AddMetadata(contextProperty + "_DefiningProjectName", EscapeStagingLiteral(captured.DefiningProjectName));
                item.AddMetadata(contextProperty + "_DefiningProjectExtension", EscapeStagingLiteral(captured.DefiningProjectExtension));
                item.AddMetadata(contextProperty + "_DefiningProjectFullPath", EscapeStagingLiteral(captured.DefiningProjectFullPath));
                item.AddMetadata(contextProperty + "_DefiningProjectDirectory", EscapeStagingLiteral(captured.DefiningProjectDirectory));
                if (captured.Type != "Reference")
                {
                    item.AddMetadata(contextProperty + "_PreparedFullPath", EscapeStagingLiteral(Path.GetFullPath(include)));
                    if (!string.IsNullOrEmpty(captured.DefiningProjectFullPath)
                        && !captured.DefiningProjectFullPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(captured.DefiningProjectDirectory)
                        && captured.SourceFullPath.StartsWith(captured.DefiningProjectDirectory, StringComparison.OrdinalIgnoreCase))
                        item.AddMetadata(contextProperty + "_ImportedLink", EscapeStagingLiteral(captured.SourceFullPath[captured.DefiningProjectDirectory.Length..]));
                }
                group.AppendChild(item);
            }
        }
    }
}
