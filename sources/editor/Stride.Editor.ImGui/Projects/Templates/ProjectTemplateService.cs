using Microsoft.TemplateEngine.Abstractions;
using Stride.Assets.Templates;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record ProjectTemplateChoice(TemplateDescription Description, ITemplateInfo? Template)
{
    private static string GetIdentity(TemplateDescription description) => description is TemplateDotNetNewDescription dotnet ? dotnet.TemplateIdentity
        : description.Id.ToString("D") + "/" + description.FullPath;
    internal string Identity { get; } = GetIdentity(Description);
    internal string WidgetIdentity { get; } = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(GetIdentity(Description)));
    internal bool OffersAssetPacks => Description is TemplateDotNetNewDescription { OffersAssetPacks: true };
}

/// <summary>Uses Stride's registered template engine and upgrades; publishes only a completed new directory.</summary>
internal static partial class ProjectTemplateService
{
    private static readonly Guid LibraryTemplateId = new("7B79F1B7-3A55-4C84-AED9-3F4F3EE4B6E5");
    public static async Task<ProjectTemplateChoice[]> Discover(bool existingSolution = false, PackageSession? sourceSession = null)
    {
        // Capture plain native metadata on the caller before asynchronous registry
        // initialization; no delayed worker reads a retired source-session graph.
        TemplateDescription[] sourceDescriptions = sourceSession == null ? [] : TemplateManager.FindTemplates(TemplateScope.Session, sourceSession).ToArray();
        try { await Task.Run(DotNetNewTemplateBridge.RegisterProjectTemplates).ConfigureAwait(false); }
        catch (Exception failure) { GlobalLogger.GetLogger("Templates").Error(failure.Message, failure); }
        var descriptions = sourceDescriptions.Concat(TemplateManager.FindTemplates(TemplateScope.Session))
            .Distinct<TemplateDescription>(ReferenceEqualityComparer.Instance).ToArray();
        return await ReadSnapshotChoices(existingSolution, descriptions).ConfigureAwait(false);
    }

    public static Task<ProjectTemplateChoice[]> ReadChoices(bool existingSolution = false, PackageSession? sourceSession = null)
        => ReadSnapshotChoices(existingSolution, TemplateManager.FindTemplates(TemplateScope.Session, sourceSession).ToArray());

    private static async Task<ProjectTemplateChoice[]> ReadSnapshotChoices(bool existingSolution, TemplateDescription[] descriptions)
    {
        var registry = DotNetNewTemplateBridge.Registry;
        IEnumerable<ITemplateInfo> templates = [];
        if (registry != null)
            try { templates = await registry.GetTemplatesAsync().ConfigureAwait(false); }
            catch (Exception failure) { GlobalLogger.GetLogger("Templates").Error(failure.Message, failure); }
        return descriptions
            .Where(description => existingSolution
                ? description.Id == LibraryTemplateId || description.FullPath?.FullPath.EndsWith("ProjectExecutable.sdtpl", StringComparison.Ordinal) == true
                : description.Id != LibraryTemplateId)
            .Where(description => !EditorExtensionGenerations.IsUnloading(description.GetType().Assembly))
            .Select(description => new ProjectTemplateChoice(description, description is TemplateDotNetNewDescription dotnet
                ? templates.FirstOrDefault(template => template.Identity == dotnet.TemplateIdentity) : null))
            .OrderBy(choice => choice.Description.Order).ThenBy(choice => choice.Description.Name).ToArray();
    }

    internal static string ValidateDestination(string name, string location)
    {
        if (string.IsNullOrWhiteSpace(location) || !UPath.IsValid(location))
            throw new ArgumentException("Invalid output directory.");
        if (string.IsNullOrWhiteSpace(name) || !UFile.IsValid(name) || name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.TrimEnd(' ', '.') != name)
            throw new ArgumentException("Invalid name.");
        string parent;
        try { parent = Path.GetFullPath(location); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Invalid output directory.", failure); }
        var destination = Path.GetFullPath(Path.Combine(parent, name));
        if (!Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(destination)!).Equals(Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid name.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Cannot use the selected name because a folder with the same name already exists in the same location.");
        return destination;
    }

    public static string Generate(ProjectTemplateChoice choice, string name, string location,
        IReadOnlyDictionary<string, string> values, IReadOnlyList<string> assetPacks, CancellationToken cancellation, ProjectSolutionSettings? solution = null)
    {
        if (choice.Template == null) throw new InvalidOperationException("A registered native template must be prepared on the editor owner actor.");
        if (solution != null) return GenerateSessionSolution(choice, name, location, values, assetPacks, cancellation, solution);
        var destination = ValidateDestination(name, location);
        var parent = Path.GetDirectoryName(destination)!;
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".stride-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var ownedMarker = Path.Combine(staging, ".stride-project-generation");
        File.WriteAllText(ownedMarker, destination);
        try
        {
            using var session = EditorPackageLoadPolicy.CreateSession();
            var log = new LoggerResult();
            var parameters = new SessionTemplateGeneratorParameters
            {
                Name = name, Namespace = name.Replace('-', '_'), Id = Guid.NewGuid(),
                OutputDirectory = new UDirectory(staging), Description = choice.Description,
                Logger = log, Session = session, Unattended = true,
            };
            DotNetNewTemplateGenerator.SetParameters(parameters, values);
            DotNetNewTemplateGenerator.SetAssetPacks(parameters, assetPacks);
            DotNetNewTemplateGenerator.SetCancellation(parameters, cancellation);
            var generator = new DotNetNewTemplateGenerator();
            if (!generator.GenerateFilesOnly(parameters) || log.HasErrors) throw new InvalidOperationException(log.ToText());
            cancellation.ThrowIfCancellationRequested();
            var open = Directory.EnumerateFiles(staging, "*.slnx", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(staging, "*.sln", SearchOption.TopDirectoryOnly))
                .Concat(Directory.EnumerateFiles(staging, "*.sdpkg", SearchOption.AllDirectories)).FirstOrDefault()
                ?? throw new IOException("The template did not create a Stride project or solution.");
            var relative = Path.GetRelativePath(staging, open);
            // Move refuses a concurrently created destination; generated files never overwrite existing work.
            Directory.Move(staging, destination);
            File.Delete(Path.Combine(destination, Path.GetFileName(ownedMarker)));
            return Path.Combine(destination, relative);
        }
        finally
        {
            // Only our unpublished staging directory, authenticated by its marker, can be removed.
            if (Directory.Exists(staging) && File.Exists(ownedMarker)
                && File.ReadAllText(ownedMarker) == destination
                && Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(staging))!).Equals(Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(staging, true);
        }
    }
}
