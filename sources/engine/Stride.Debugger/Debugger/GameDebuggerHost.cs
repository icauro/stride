// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Threading.Tasks;
using ServiceWire.NamedPipes;
using Stride.Core.Diagnostics;

namespace Stride.Debugger.Target
{
    public class GameDebuggerHost : IGameDebuggerHost
    {
        private TaskCompletionSource<IGameDebuggerTarget> target = new TaskCompletionSource<IGameDebuggerTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        private NpClient<IGameDebuggerTarget> callbackChannel;
        private readonly object gate = new object();
        private bool disposed;

        public event Action GameExited;

        public LoggerResult Log { get; private set; }

        public GameDebuggerHost(LoggerResult logger)
        {
            Log = logger;
        }

        public Task<IGameDebuggerTarget> Target
        {
            get { return target.Task; }
        }

        public void RegisterTarget(string callbackAddress)
        {
            lock (gate)
            {
                if (disposed) return;
                var channel = new NpClient<IGameDebuggerTarget>(new NpEndPoint(callbackAddress));
                if (!target.TrySetResult(channel.Proxy)) { channel.Dispose(); return; }
                callbackChannel = channel;
            }
        }

        public void OnGameExited()
        {
            GameExited?.Invoke();
        }

        public void OnLogMessage(SerializableLogMessage logMessage)
        {
            Log.Log(logMessage);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; target.TrySetCanceled();
                callbackChannel?.Dispose(); callbackChannel = null;
            }
        }
    }
}
