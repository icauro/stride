// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ServiceWire.NamedPipes;
using Stride.Core.Diagnostics;
using Stride.Core.MicroThreading;
using Stride.Core.Reflection;
using Stride.Engine;
using Stride.Engine.Processors;

namespace Stride.Debugger.Target
{
    public class GameDebuggerTarget : IGameDebuggerTarget
    {
        private static readonly Logger Log = GlobalLogger.GetLogger("GameDebuggerSession");

        /// <summary>
        /// The assembly container, to load assembly without locking main files.
        /// </summary>
        // For now, it uses default one, but later we should probably have one per game debugger session
        private readonly AssemblyContainer assemblyContainer = AssemblyContainer.Default;

        private string projectName = String.Empty;
        private readonly Dictionary<DebugAssembly, Assembly> loadedAssemblies = new Dictionary<DebugAssembly, Assembly>();
        private HashSet<DebugAssembly> activeAssemblies = new HashSet<DebugAssembly>();
        private Dictionary<string, string> resolutionPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        private int currentDebugAssemblyIndex;
        private Game game;
        private readonly object updates = new object();
        private GameRun gameRun;
        private sealed class GameRun
        {
            public Game Instance;
            public readonly ManualResetEventSlim Finished = new ManualResetEventSlim(false);
            public volatile bool IntentionalExit;
            public volatile bool SuppressExitNotification;
            public Exception Failure;
        }
        private IGameDebuggerHost host;

        /// <summary>
        /// Flag if exit was requested.
        /// </summary>
        /// <remarks>Field is volatile to avoid compiler optimization that would prevent MainLoop from exiting.</remarks>
        private volatile bool requestedExit;

        public GameDebuggerTarget()
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            // Make sure this assembly is registered (it contains custom Yaml serializers such as CloneReferenceSerializer)
            // Note: this assembly should not be registered when run by Game Studio
            AssemblyRegistry.Register(typeof(Program).GetTypeInfo().Assembly, AssemblyCommonCategories.Assets);
        }

        Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            lock (loadedAssemblies)
            {
                var active = activeAssemblies.Select(id => loadedAssemblies[id]).FirstOrDefault(assembly => assembly.FullName == args.Name);
                if (active != null) return active;
                return resolutionPaths.TryGetValue(args.Name, out var path) ? assemblyContainer.LoadAssemblyFromPath(path) : null;
            }
        }
        public void SetAssemblyResolutionPaths(string[] paths)
        {
            var captured = paths.Select(System.IO.Path.GetFullPath).GroupBy(path => AssemblyName.GetAssemblyName(path).FullName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            lock (loadedAssemblies) resolutionPaths = captured;
        }
        public bool GameIsRunning() => Volatile.Read(ref game)?.IsRunning == true;

        /// <inheritdoc/>
        public void Exit()
        {
            requestedExit = true;
        }

        /// <inheritdoc/>
        public DebugAssembly AssemblyLoad(string assemblyPath)
        {
            try
            {
                var assembly = assemblyContainer.LoadAssemblyFromPath(assemblyPath);
                if (assembly == null)
                {
                    Log.Error($"Unexpected error while loading assembly reference [{assemblyPath}] in project [{projectName}]");
                    return DebugAssembly.Empty;
                }

                return CreateDebugAssembly(assembly);
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected error while loading assembly reference [{assemblyPath}] in project [{projectName}]", ex);
                return DebugAssembly.Empty;
            }
        }

        /// <inheritdoc/>
        public DebugAssembly AssemblyLoadRaw(byte[] peData, byte[] pdbData)
        {
            try
            {
                lock (loadedAssemblies)
                {
                    var assembly = Assembly.Load(peData, pdbData);
                    return CreateDebugAssembly(assembly);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected error while loading assembly reference in project [{projectName}]", ex);
                return DebugAssembly.Empty;
            }
        }

        /// <inheritdoc/>
        public bool AssemblyUpdate(List<DebugAssembly> assembliesToUnregister, List<DebugAssembly> assembliesToRegister)
        {
            Log.Info("Reloading assemblies and updating scripts");

            // Serialize native update requests, but do not hold the resolver's
            // dictionary lock while waiting for the game thread or its TickLock.
            lock (updates)
            {
                HashSet<DebugAssembly> before, desired;
                List<Assembly> previous, next;
                lock (loadedAssemblies)
                {
                    before = activeAssemblies; desired = new HashSet<DebugAssembly>(before);
                    foreach (var id in assembliesToUnregister) if (!desired.Remove(id)) throw new InvalidOperationException("The live assembly is not active.");
                    foreach (var id in assembliesToRegister)
                        if (id.Equals(DebugAssembly.Empty) || !loadedAssemblies.ContainsKey(id) || !desired.Add(id)) throw new InvalidOperationException("The replacement live assembly is unavailable.");
                    previous = assembliesToUnregister.Select(id => loadedAssemblies[id]).ToList();
                    next = assembliesToRegister.Select(id => loadedAssemblies[id]).ToList();
                }
                var current = game;
                if (current != null && GameRequiresRestart(current.GetType(), previous))
                {
                    var name = current.GetType().FullName;
                    StopGameCore();
                    var switched = false;
                    try
                    {
                        lock (loadedAssemblies) activeAssemblies = desired;
                        LiveAssemblyReloader.Reload(null, assemblyContainer, previous, next); switched = true;
                        LaunchGameCore(name, suppressExit: true); WaitForGameStart(); CommitGameStart();
                    }
                    catch (Exception failure)
                    {
                        var recovery = new List<Exception>();
                        try { StopGameCore(); } catch (Exception error) { recovery.Add(error); }
                        lock (loadedAssemblies) activeAssemblies = before;
                        if (switched)
                            try { LiveAssemblyReloader.Reload(null, assemblyContainer, next, previous); } catch (Exception error) { recovery.Add(error); }
                        if (recovery.Count == 0)
                            try { LaunchGameCore(name, suppressExit: true); WaitForGameStart(); CommitGameStart(); } catch (Exception error) { recovery.Add(error); }
                        if (recovery.Count != 0) { recovery.Insert(0, failure); throw new AggregateException("The live game restart and recovery failed.", recovery); }
                        throw;
                    }
                }
                else
                {
                    void Apply()
                    {
                        lock (loadedAssemblies) activeAssemblies = desired;
                        try { LiveAssemblyReloader.Reload(current, assemblyContainer, previous, next); }
                        catch { lock (loadedAssemblies) activeAssemblies = before; throw; }
                    }
                    if (current == null) Apply(); else lock (current.TickLock) Apply();
                }
            }
            return true;
        }

        /// <inheritdoc/>
        public List<string> GameEnumerateTypeNames()
        {
            lock (loadedAssemblies)
            {
                return GameEnumerateTypesHelper().Select(x => x.FullName).ToList();
            }
        }

        /// <inheritdoc/>
        public void GameLaunch(string gameTypeName)
        {
            lock (updates) LaunchGameCore(gameTypeName);
        }

        /// <inheritdoc/>
        public void GameStop()
        {
            lock (updates) StopGameCore();
        }

        private static bool GameRequiresRestart(Type type, List<Assembly> previous)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (previous.Contains(current.Assembly) || current.IsGenericType && current.GetGenericArguments().Any(argument => previous.Contains(argument.Assembly))) return true;
            return false;
        }
        private void LaunchGameCore(string name, bool suppressExit = false)
        {
            if (game != null || gameRun != null && !gameRun.Finished.IsSet) throw new InvalidOperationException("A game is already running in this live session.");
            Type type;
            lock (loadedAssemblies) type = GameEnumerateTypesHelper().SingleOrDefault(candidate => candidate.FullName == name);
            if (type == null) throw new InvalidOperationException("Could not find type [" + name + "] in project [" + projectName + "]");
            var run = new GameRun { Instance = (Game)Activator.CreateInstance(type), SuppressExitNotification = suppressExit };
            gameRun = run; Volatile.Write(ref game, run.Instance);
            Log.Info("Running game with type " + name);
            Task.Run(() =>
            {
                try
                {
                    using (run.Instance)
                    {
                        run.Instance.Script.Scheduler.PropagateExceptions = false;
                        run.Instance.Run();
                    }
                }
                catch (Exception error) { run.Failure = error; Log.Error("Exception while running game", error); }
                finally
                {
                    Interlocked.CompareExchange(ref game, null, run.Instance);
                    try { if (!run.IntentionalExit && !run.SuppressExitNotification) host.OnGameExited(); }
                    finally { run.Finished.Set(); }
                }
            });
        }
        private void StopGameCore()
        {
            var run = gameRun;
            if (run == null) return;
            run.IntentionalExit = true;
            if (!run.Finished.IsSet) run.Instance.Exit();
            if (!run.Finished.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The running game did not stop for live assembly replacement.");
            Interlocked.CompareExchange(ref game, null, run.Instance);
            gameRun = null; run.Finished.Dispose();
        }
        private void WaitForGameStart()
        {
            var run = gameRun ?? throw new InvalidOperationException("The live game was not created.");
            var deadline = Stopwatch.StartNew();
            while (!run.Instance.IsRunning)
            {
                if (run.Finished.IsSet) throw new InvalidOperationException("The replacement live game exited during startup.", run.Failure);
                if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("The replacement live game did not start.");
                Thread.Sleep(10);
            }
        }
        private void CommitGameStart()
        {
            var run = gameRun ?? throw new InvalidOperationException("The replacement live game is no longer owned.");
            run.SuppressExitNotification = false;
            if (run.Finished.IsSet && !run.IntentionalExit) host.OnGameExited();
        }

        private IEnumerable<Type> GameEnumerateTypesHelper()
        {
            // We enumerate custom games, and then typeof(Game) as fallback
            return activeAssemblies.Select(id => loadedAssemblies[id]).SelectMany(assembly => assembly.GetTypes().Where(type => !type.IsAbstract && typeof(Game).IsAssignableFrom(type)))
                .Concat(Enumerable.Repeat(typeof(Game), 1)).Distinct();
        }

        private DebugAssembly CreateDebugAssembly(Assembly assembly)
        {
            var debugAssembly = new DebugAssembly(++currentDebugAssemblyIndex);
            loadedAssemblies.Add(debugAssembly, assembly);
            return debugAssembly;
        }

        public void MainLoop(IGameDebuggerHost gameDebuggerHost)
        {
            host = gameDebuggerHost;
            string callbackChannelEndpoint = "Stride/Debugger/GameDebuggerTarget/" + Guid.NewGuid().ToString("N");
            using (var callbackHost = new NpHost(callbackChannelEndpoint, null, null))
            {
                callbackHost.AddService<IGameDebuggerTarget>(this);
                callbackHost.Open();
                host.RegisterTarget(callbackChannelEndpoint);

                Action<ILogMessage> forward = message => Log_MessageLogged(null, new MessageLoggedEventArgs(message));
                GlobalLogger.GlobalMessageLogged += forward;
                try { Log.Info("Starting debugging session"); while (!requestedExit) Thread.Sleep(10); }
                finally { GlobalLogger.GlobalMessageLogged -= forward; }
            }
        }

        void Log_MessageLogged(object sender, MessageLoggedEventArgs e)
        {
            var message = e.Message;

            var serializableMessage = message as SerializableLogMessage;
            if (serializableMessage == null)
            {
                var logMessage = message as LogMessage;
                if (logMessage != null)
                {
                    serializableMessage = new SerializableLogMessage(logMessage);
                }
            }

            if (serializableMessage == null)
            {
                throw new InvalidOperationException(@"Unable to process the given log message.");
            }

            host.OnLogMessage(serializableMessage);
        }
    }
}
