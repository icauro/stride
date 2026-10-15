namespace Stride.Editor.ImGui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (EditorGraphicsBackend.Initialize(args)) RunEditor(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunEditor(string[] args)
    {
        WindowsDpi.EnablePerMonitorAwareness();
        NugetLibraryParityFixture.ConfigureP02NugetLibraryFixtureBeforeBootstrap(args);
        if (args.Contains("--ui-test")) File.WriteAllText(UiWorkflowTest.WorkflowTracePath, "UI workflow test started" + Environment.NewLine);
        Stride.Core.Assets.PackageSessionPublicHelper.FindAndSetMSBuildVersion();
        EditorBuiltInModules.Register();
        EntityCreationFactories.RegisterComponentAssemblies();
        var recoveryProducer = Array.IndexOf(args, "--recovery-completion-produce");
        if (recoveryProducer >= 0)
        {
            if (recoveryProducer + 1 >= args.Length) throw new ArgumentException("Recovery completion producer requires a fresh private directory.");
            RecoveryCompletionContracts.ProduceAsync(args[recoveryProducer + 1]).GetAwaiter().GetResult();
            return;
        }
        var recoveryReader = Array.IndexOf(args, "--recovery-completion-verify");
        if (recoveryReader >= 0)
        {
            if (recoveryReader + 2 >= args.Length) throw new ArgumentException("Recovery completion reader requires the producer directory and a fresh output file.");
            var evidence = RecoveryCompletionContracts.Verify(args[recoveryReader + 1]);
            var output = Path.GetFullPath(args[recoveryReader + 2]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            System.Text.Json.JsonSerializer.Serialize(stream, evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return;
        }
        var parityService = Array.IndexOf(args, "--verify-parity-service");
        if (parityService >= 0)
        {
            if (parityService + 2 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --verify-parity-service <dependencies|models|progress|cross-volume> <artifacts-directory>");
                Environment.ExitCode = 2;
                return;
            }
            Environment.ExitCode = ParityServiceVerification.Run(args[parityService + 1], args[parityService + 2]);
            return;
        }
        var prepareAuthored = Array.IndexOf(args, "--prepare-authored-fixture");
        if (prepareAuthored >= 0)
        {
            if (prepareAuthored + 2 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --prepare-authored-fixture <source-package> <receipt-json-path>");
                Environment.ExitCode = 2;
                return;
            }
            Environment.ExitCode = PreparedAuthoredFixture.PrepareCopy(args[prepareAuthored + 1], args[prepareAuthored + 2]);
            return;
        }
        var capability = Array.IndexOf(args, "--verify-capability");
        if (capability >= 0)
        {
            if (capability + 3 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --verify-capability <scenario> <package> <artifacts-directory>");
                Environment.ExitCode = 2;
                return;
            }
            var scenario = args[capability + 1];
            Environment.ExitCode = ProjectCapabilityScenarios.Cases.Contains(scenario)
                ? ProjectCapabilityScenarios.Run(scenario, args[capability + 2], args[capability + 3])
                : CoreCapabilityScenarios.Run(scenario, args[capability + 2], args[capability + 3]);
            return;
        }
        var verify = Array.IndexOf(args, "--verify-document");
        if (verify >= 0 && verify + 1 < args.Length)
        {
            Environment.ExitCode = DocumentRegression.Run(args[verify + 1]);
            return;
        }
        using var game = new EditorGame(args);
        try
        {
            if (WindowsBackgroundLaunch.Enabled) game.Run(WindowsBackgroundLaunch.CreateContext());
            else game.Run();
        }
        catch (Exception error) { Console.Error.WriteLine("Editor run failed: " + error); throw; }
    }
}
