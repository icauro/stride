using System.Security.Cryptography;
using Stride.Assets.Templates;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static partial class ProjectTemplateService
{
    private static string GenerateSessionSolution(ProjectTemplateChoice choice, string name, string location,
        IReadOnlyDictionary<string, string> values, IReadOnlyList<string> assetPacks, CancellationToken cancellation, ProjectSolutionSettings solution)
    {
        var destination = ValidateDestination(name, location);
        var solutionPath = ValidateSolutionDestination(name, location, solution);
        cancellation.ThrowIfCancellationRequested();
        var publication = new ProjectTemplatePublication(destination);
        try
        {
            byte[]? externalBytes = null;
            using (var session = EditorPackageLoadPolicy.CreateSession())
            {
                var log = new LoggerResult();
                var parameters = new SessionTemplateGeneratorParameters
                {
                    Name = name, Namespace = name.Replace('-', '_'), Id = Guid.NewGuid(), OutputDirectory = new UDirectory(publication.Stage),
                    Description = choice.Description, Logger = log, Session = session, Unattended = true,
                };
                DotNetNewTemplateGenerator.SetParameters(parameters, values);
                DotNetNewTemplateGenerator.SetAssetPacks(parameters, assetPacks);
                DotNetNewTemplateGenerator.SetCancellation(parameters, cancellation);
                var generator = new DotNetNewTemplateGenerator();
                if (!generator.GenerateFilesOnly(parameters) || log.HasErrors) throw new InvalidOperationException(log.ToText());
                cancellation.ThrowIfCancellationRequested();
                var generatedSolution = Directory.EnumerateFiles(publication.Stage, "*.slnx", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(publication.Stage, "*.sln", SearchOption.TopDirectoryOnly)).FirstOrDefault()
                    ?? throw new IOException("The template did not create a solution model.");
                session.AdoptGeneratedSolution(generatedSolution);
                var serialized = Path.Combine(publication.Stage, ".stride-solution-" + Guid.NewGuid().ToString("N") + ".slnx");
                session.StageGeneratedSolution(serialized, solutionPath, publication.Stage, destination);
                cancellation.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(serialized);
                // The native session generator skips the template solution. Here
                // its actual model supplied the session graph; publish only the
                // canonical graph serialization at the requested final location.
                DeletePrivateOutput(generatedSolution);
                if (ProjectTemplatePublication.Inside(destination, solutionPath))
                {
                    var stageSolution = Path.Combine(publication.Stage, Path.GetRelativePath(destination, solutionPath));
                    var parentIdentity = publication.EnsureDirectory(Path.GetDirectoryName(stageSolution)!);
                    WindowsOwnedAssetFileWriter.WriteNewInDirectory(stageSolution, parentIdentity, bytes);
                }
                else externalBytes = bytes;
                DeletePrivateOutput(serialized);
            }
            // Native session teardown completes before either output is adopted.
            return publication.Publish(solutionPath, externalBytes, cancellation);
        }
        catch (Exception failure)
        {
            publication.Compensate(failure);
            throw;
        }
    }
    internal static void DeletePrivateOutput(string path)
    {
        byte[] hash; OwnedFileIdentity identity;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { identity = WindowsOwnedAssetFileWriter.CaptureIdentity(stream); hash = SHA256.HashData(stream); }
        WindowsOwnedAssetFileDeletion.Delete(path, hash, true, identity);
    }
}
