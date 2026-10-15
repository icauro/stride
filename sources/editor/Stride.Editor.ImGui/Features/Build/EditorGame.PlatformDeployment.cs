using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;
using ImGuiP = Hexa.NET.ImGui.ImGuiP;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PlatformRunRequest(string Project, string Configuration, string Framework, string Solution, PlatformType Platform);
    private Task<DesktopProjectBuildResult?>? platformRunBuild;
    private Task? platformRunDeployment;
    private CancellationTokenSource? platformRunCancellation;
    private DesktopProjectBuildResult? platformRunOutput;
    private ProjectWorkspace? platformRunWorkspace;
    private RemoteGameDeployment? remoteGameDeployment;
    private Task<RemoteCredentialResult>? remoteCredentialsCheck;
    private CancellationTokenSource? remoteCredentialsCancellation;
    private RemoteGameCredentials? testedRemoteCredentials;
    private string remoteHost = "", remoteUsername = "", remotePassword = "", remoteLocation = "", remoteCredentialsMessage = "";
    private int remotePort = 22;
    private bool remoteCredentialsOpen, remoteCredentialsDontAsk, remoteCredentialsSaveRequested;
    private bool IsPlatformDeploymentUpdating => platformRunBuild != null || platformRunDeployment != null || remoteCredentialsOpen;

    private void StartPlatformProjectRun()
    {
        if (!CanStartUserProjectBuild || buildTarget == null || workspace == null) return;
        SaveForUserProjectBuild(saved =>
        {
        StopPlatformDeployment();
        var request = new PlatformRunRequest(saved.Project, saved.Configuration, saved.Framework, saved.Solution, saved.Platform);
        platformRunWorkspace = workspace; platformRunCancellation = new(); var token = platformRunCancellation.Token;
        focusOutput = true; statusMessage = "Build in progress…";
        platformRunBuild = Task.Run(async () =>
        {
            if (request.Platform == PlatformType.Android)
            {
                await DesktopProjectBuild.BuildAndRunAndroid(request.Project, request.Configuration, request.Framework, request.Solution, token, RecordBuildOutput);
                return (DesktopProjectBuildResult?)null;
            }
            var platform = request.Platform switch { PlatformType.Linux => "Linux", PlatformType.macOS => "macOS", _ => throw new InvalidOperationException("Platform " + request.Platform + " isn't supported for execution.") };
            return await DesktopProjectBuild.Build(request.Project, request.Configuration, request.Framework, request.Solution, token, RecordBuildOutput, platform);
        }, token);
        });
    }

    private void PumpPlatformDeployment()
    {
        if (platformRunWorkspace != null && !ReferenceEquals(platformRunWorkspace, workspace)) StopPlatformDeployment();
        if (remoteGameDeployment?.HasExited == true) { remoteGameDeployment.Dispose(); remoteGameDeployment = null; }
        if (platformRunBuild?.IsCompleted == true)
        {
            var completed = platformRunBuild; platformRunBuild = null;
            try
            {
                platformRunOutput = completed.GetAwaiter().GetResult();
                statusMessage = "Build succeeded";
                if (platformRunOutput != null && !closeRequested && !closePromptOpen && !exitApproved)
                {
                    var current = preferences!.Current;
                    if (current.AskForRemoteCredentials || string.IsNullOrWhiteSpace(current.RemoteHost))
                    {
                        remoteHost = current.RemoteHost; remotePort = current.RemotePort; remoteUsername = current.RemoteUsername;
                        remotePassword = RemoteGameDeployment.DecryptPassword(current.RemotePassword); remoteLocation = current.RemoteLocation;
                        remoteCredentialsDontAsk = !current.AskForRemoteCredentials; remoteCredentialsMessage = ""; remoteCredentialsOpen = true;
                    }
                    else StartRemoteProjectDeployment(new(current.RemoteHost, current.RemotePort, current.RemoteUsername,
                        RemoteGameDeployment.DecryptPassword(current.RemotePassword), current.RemoteLocation, current.RemoteDisplay));
                }
            }
            catch (Exception error) { Report(error); StopPlatformDeployment(); }
            if (platformRunOutput == null) { platformRunCancellation?.Dispose(); platformRunCancellation = null; }
        }
        if (remoteCredentialsCheck?.IsCompleted == true)
        {
            var completed = remoteCredentialsCheck; remoteCredentialsCheck = null;
            remoteCredentialsCancellation?.Dispose(); remoteCredentialsCancellation = null;
            try
            {
                var result = completed.GetAwaiter().GetResult();
                if (testedRemoteCredentials == CurrentRemoteCredentials())
                {
                    remoteCredentialsMessage = EditorLocalization.Text(result switch
                    {
                        RemoteCredentialResult.Valid => "Your credentials are correct.",
                        RemoteCredentialResult.InvalidHost => "Couldn't reach the specified host.",
                        RemoteCredentialResult.InvalidCredentials => "Invalid credentials.",
                        RemoteCredentialResult.InvalidLocation => "The location you specified doesn't exist.",
                        _ => "An unknown error occurred.",
                    }, "Credentials");
                    if (remoteCredentialsSaveRequested && result == RemoteCredentialResult.Valid)
                    {
                        var current = preferences!.Current;
                        current.RemoteHost = remoteHost; current.RemotePort = remotePort; current.RemoteUsername = remoteUsername;
                        current.RemotePassword = RemoteGameDeployment.EncryptPassword(remotePassword); current.RemoteLocation = remoteLocation;
                        current.AskForRemoteCredentials = !remoteCredentialsDontAsk; SaveEditorPreferences();
                        remoteCredentialsOpen = false; StartRemoteProjectDeployment(testedRemoteCredentials);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
            finally { remoteCredentialsSaveRequested = false; }
        }
        if (platformRunDeployment?.IsCompleted == true)
        {
            var completed = platformRunDeployment; platformRunDeployment = null;
            platformRunCancellation?.Dispose(); platformRunCancellation = null;
            try { completed.GetAwaiter().GetResult(); statusMessage = "Project started"; }
            catch (Exception error) { Report(error); StopPlatformDeployment(); }
        }
        void Report(Exception error) => outputLog.Append("Build", error is OperationCanceledException ? LogMessageType.Info : LogMessageType.Error, "Deployment", error.ToString());
    }

    private RemoteGameCredentials CurrentRemoteCredentials() => new(remoteHost, remotePort, remoteUsername, remotePassword, remoteLocation, preferences?.Current.RemoteDisplay ?? ":0.0");
    private void StartRemoteProjectDeployment(RemoteGameCredentials credentials)
    {
        var output = platformRunOutput ?? throw new InvalidOperationException("There is no completed native project output.");
        var logger = new LoggerResult(); logger.MessageLogged += (_, args) => outputLog.Append("Build", args.Message.Type, "Deployment", args.Message.ToString());
        var deployment = new RemoteGameDeployment(); remoteGameDeployment = deployment;
        var token = platformRunCancellation!.Token;
        platformRunDeployment = Task.Run(() => deployment.Launch(output.TargetPath, credentials, logger, token), token);
        platformRunOutput = null;
    }

    private void DrawPlatformDeploymentPrompt()
    {
        var popupName = EditorLocalization.Widget("Credentials", "Credentials");
        if (remoteCredentialsOpen && !Gui.IsPopupOpen(popupName)) Gui.OpenPopup(popupName);
        Gui.SetNextWindowSize(new(UiScale(450), UiScale(425)), ImGuiCond.Appearing);
        Gui.SetNextWindowSizeConstraints(new(UiScale(450), UiScale(420)), new(float.MaxValue, float.MaxValue));
        using var modal = ImGuiScopes.Modal(popupName, ImGuiWindowFlags.NoCollapse);
        if (!modal.DrawContents) return;
        if (!remoteCredentialsOpen) { Gui.CloseCurrentPopup(); return; }
        var submitRequested = false;
        Gui.TextWrapped(EditorLocalization.Text("Provide information about the host to connect to. Your password is encrypted on this machine.", "Credentials"));
        NativeStringInput.Draw(EditorLocalization.Widget("Host", "Credentials"), ref remoteHost);
        submitRequested |= SubmitField();
        Gui.InputInt(EditorLocalization.Widget("Port", "Credentials"), ref remotePort);
        submitRequested |= SubmitField();
        NativeStringInput.Draw(EditorLocalization.Widget("Username", "Credentials"), ref remoteUsername);
        submitRequested |= SubmitField();
        NativeStringInput.Draw(EditorLocalization.Widget("Password", "Credentials"), ref remotePassword, ImGuiInputTextFlags.Password);
        submitRequested |= SubmitField();
        NativeStringInput.Draw(EditorLocalization.Widget("Location", "Credentials"), ref remoteLocation);
        submitRequested |= SubmitField();
        DrawNativeCheckbox(EditorLocalization.Widget("Don't ask again", "Credentials"), ref remoteCredentialsDontAsk);
        if (!string.IsNullOrEmpty(remoteCredentialsMessage))
            DrawSelectableText("Credentials result", remoteCredentialsMessage, true, new System.Numerics.Vector2(Gui.GetContentRegionAvail().X, UiScale(65)));
        var canCheck = remoteCredentialsCheck == null && !string.IsNullOrWhiteSpace(remoteHost) && !string.IsNullOrWhiteSpace(remoteUsername)
            && !string.IsNullOrWhiteSpace(remoteLocation) && remotePort is >= 1 and <= 65535;
        using (ImGuiScopes.Disabled(!canCheck))
        {
            if (Gui.Button(EditorLocalization.Widget("Test settings", "Credentials"))) Check(false);
            Gui.SameLine();
            if (Gui.Button(EditorLocalization.Widget("Save", "Button")) || canCheck && submitRequested) Check(true);
        }
        Gui.SameLine();
        var cancelLabel = EditorLocalization.Widget("Cancel", "Button");
        var cancelKey = OwnsInput() && !Gui.GetIO().WantTextInput && !Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.Escape, false);
        if (Gui.Button(cancelLabel) || cancelKey)
        {
            if (cancelKey) ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetID(cancelLabel), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            StopPlatformDeployment(); Gui.CloseCurrentPopup();
        }
        bool OwnsInput() => !Gui.GetIO().AppFocusLost && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId) && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper && !Gui.GetIO().KeyShift;
        bool SubmitField()
        {
            if (!Gui.IsItemFocused() || !OwnsInput() || !(Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false))) return false;
            var owner = Gui.GetItemID();
            ImGuiP.SetKeyOwner(ImGuiKey.Enter, owner, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            ImGuiP.SetKeyOwner(ImGuiKey.KeypadEnter, owner, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            return true;
        }
        void Check(bool save)
        {
            if (remoteCredentialsCheck != null) return;
            remoteCredentialsCancellation = new(); var token = remoteCredentialsCancellation.Token;
            testedRemoteCredentials = CurrentRemoteCredentials(); remoteCredentialsSaveRequested = save; remoteCredentialsMessage = "";
            var snapshot = testedRemoteCredentials;
            remoteCredentialsCheck = Task.Run(() => RemoteGameDeployment.Test(snapshot, token), token);
        }
    }

    private void StopPlatformDeployment()
    {
        var deployment = remoteGameDeployment;
        if (deployment != null)
        {
            deployment.RequestCancellation();
            EditorBackgroundJobRetirement.Retire(RetainRemoteDeploymentUntilInitialized(platformRunDeployment, deployment),
                platformRunCancellation, retired => retired.Dispose(), "Remote deployment initialization");
        }
        remoteGameDeployment = null;
        DetachBuildTask(platformRunBuild, deployment == null ? platformRunCancellation : null);
        if (deployment == null) DetachBuildTask(platformRunDeployment, null);
        DetachBuildTask(remoteCredentialsCheck, remoteCredentialsCancellation);
        platformRunBuild = null; platformRunDeployment = null; platformRunCancellation = null;
        remoteCredentialsCheck = null; remoteCredentialsCancellation = null; testedRemoteCredentials = null;
        platformRunOutput = null; platformRunWorkspace = null; remoteCredentialsOpen = false; remoteCredentialsSaveRequested = false;
        remotePassword = ""; remoteCredentialsMessage = "";
    }

    private static async Task<RemoteGameDeployment> RetainRemoteDeploymentUntilInitialized(Task? initialization, RemoteGameDeployment deployment)
    {
        if (initialization != null)
        {
            try { await initialization.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { EditorBackgroundJobRetirement.ReportFailure("Remote deployment initialization ended during retirement", error); }
        }
        return deployment;
    }
}
