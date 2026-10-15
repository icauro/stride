using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed record RemoteGameCredentials(string Host, int Port, string Username, string Password, string Location, string Display);
internal enum RemoteCredentialResult { Valid, InvalidHost, InvalidCredentials, InvalidLocation }

/// <summary>The WPF SSH/SFTP deployment workflow, independent of the WPF settings and modal services.</summary>
internal sealed class RemoteGameDeployment : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private SshClient? client;
    private SshCommand? command;
    private Task? execution;
    private int disposed;
    private bool lifetimeDisposed;
    public bool HasExited => execution?.IsCompleted == true;
    internal void RequestCancellation() => lifetime.Cancel();
    public static string EncryptPassword(string password) => Convert.ToBase64String(ProtectedData.Protect(Encoding.Unicode.GetBytes(password), null, DataProtectionScope.CurrentUser));
    public static string DecryptPassword(string password)
    {
        try { return Encoding.Unicode.GetString(ProtectedData.Unprotect(Convert.FromBase64String(password), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is FormatException or CryptographicException) { return ""; }
    }
    private static ConnectionInfo Connection(RemoteGameCredentials credentials)
    {
        var password = new PasswordAuthenticationMethod(credentials.Username, credentials.Password);
        var interactive = new KeyboardInteractiveAuthenticationMethod(credentials.Username);
        interactive.AuthenticationPrompt += (_, args) =>
        {
            foreach (var prompt in args.Prompts)
                if (prompt.Request.Contains("Password:", StringComparison.InvariantCultureIgnoreCase)) prompt.Response = credentials.Password;
        };
        return new ConnectionInfo(credentials.Host, credentials.Port, credentials.Username, password, interactive)
            { Timeout = TimeSpan.FromSeconds(20) };
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public static async Task<RemoteCredentialResult> Test(RemoteGameCredentials credentials, CancellationToken cancellation)
    {
        try
        {
            using var client = new SshClient(Connection(credentials));
            await client.ConnectAsync(cancellation).ConfigureAwait(false);
            using var command = client.CreateCommand("cd -- " + Quote(credentials.Location));
            await command.ExecuteAsync(cancellation).ConfigureAwait(false);
            return command.ExitStatus == 0 ? RemoteCredentialResult.Valid : RemoteCredentialResult.InvalidLocation;
        }
        catch (SshAuthenticationException) { return RemoteCredentialResult.InvalidCredentials; }
        catch (Exception error) when (error is SshException or SocketException) { return RemoteCredentialResult.InvalidHost; }
    }

    public async Task Launch(string assemblyPath, RemoteGameCredentials credentials, LoggerResult logger, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        var source = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        var destination = credentials.Location.TrimEnd('/') + "/" + Path.GetFileNameWithoutExtension(assemblyPath);
        using (var sftp = new SftpClient(Connection(credentials)))
        {
            await sftp.ConnectAsync(token).ConfigureAwait(false);
            var directories = new Queue<string>(); directories.Enqueue(source);
            while (directories.TryDequeue(out var directory))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, directory).Replace('\\', '/');
                var remote = relative == "." ? destination : destination + "/" + relative;
                if (!await sftp.ExistsAsync(remote, token).ConfigureAwait(false)) await sftp.CreateDirectoryAsync(remote, token).ConfigureAwait(false);
                logger.Log(new LogMessage("Deployment", LogMessageType.Info, "Synchronizing " + directory + " with " + remote));
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("The build output contains a redirected directory: " + child);
                    directories.Enqueue(child);
                }
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The build output contains a redirected file: " + path);
                    var remotePath = remote + "/" + Path.GetFileName(path);
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await sftp.UploadFileAsync(file, remotePath, token).ConfigureAwait(false);
                    sftp.ChangePermissions(remotePath, 755);
                    logger.Log(new LogMessage("Deployment", LogMessageType.Info, "Updating " + Path.GetFileName(path)));
                }
            }
        }
        client = new SshClient(Connection(credentials));
        await client.ConnectAsync(token).ConfigureAwait(false);
        using (var setup = client.CreateCommand("bash -lc " + Quote("source /etc/profile; cd -- " + Quote(destination) + "; sh ./CoreCLRSetup.sh")))
        {
            await setup.ExecuteAsync(token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(setup.Error)) logger.Log(new LogMessage("Deployment", LogMessageType.Error, setup.Error));
            if (!string.IsNullOrEmpty(setup.Result)) logger.Log(new LogMessage("Deployment", LogMessageType.Info, setup.Result));
        }
        var localAddress = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up
                && item.GetIPProperties().DhcpServerAddresses.Count != 0
                && item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses).FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        var environment = "DISPLAY=" + Quote(string.IsNullOrEmpty(credentials.Display) ? ":0.0" : credentials.Display);
        if (localAddress != null) environment += " StrideConnectionRouterRemoteIP=" + Quote(localAddress);
        command = client.CreateCommand("bash -lc " + Quote("source /etc/profile; cd -- " + Quote(destination) + "; "
            + environment + " dotnet " + Quote("./" + Path.GetFileName(assemblyPath))));
        command.CommandTimeout = Timeout.InfiniteTimeSpan;
        // The native command owns the remote game lifetime, rather than a fabricated local PID.
        execution = Observe(command.ExecuteAsync(lifetime.Token), command, logger);
        token.ThrowIfCancellationRequested();
    }
    private static async Task Observe(Task task, SshCommand command, LoggerResult logger)
    {
        try
        {
            await task.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(command.Result)) logger.Log(new LogMessage("Deployment", LogMessageType.Info, command.Result));
            if (!string.IsNullOrEmpty(command.Error)) logger.Log(new LogMessage("Deployment", LogMessageType.Error, command.Error));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { logger.Log(new LogMessage("Deployment", LogMessageType.Error, error.ToString())); }
    }
    public void Dispose()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        RequestCancellation();
        if (Interlocked.CompareExchange(ref disposed, 1, 0) != 0) return;
        EditorBackgroundJobRetirement.Retire(RetainUntilExecutionEnds(execution, this), null,
            owner => owner.ReleaseNativeResources(), "Remote deployment execution");
    }

    private static async Task<RemoteGameDeployment> RetainUntilExecutionEnds(Task? task, RemoteGameDeployment owner)
    {
        if (task != null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { EditorBackgroundJobRetirement.ReportFailure("Remote deployment ended during retirement", error); }
        }
        return owner;
    }

    private void ReleaseNativeResources()
    {
        if (command != null) { command.Dispose(); command = null; }
        if (client != null) { client.Dispose(); client = null; }
        if (!lifetimeDisposed) { lifetime.Dispose(); lifetimeDisposed = true; }
    }
}
