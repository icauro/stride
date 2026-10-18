// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;

internal static class QueryValidation
{
    internal static void Run(GraphicsDevice device)
    {
        if (device.TimestampFrequency == 0)
        {
            Console.WriteLine("SKIP: Adapter does not expose native encoder timestamps.");
            return;
        }
        using var pool = QueryPool.New(device, QueryType.Timestamp, 4);
        using var commands = CommandList.New(device);
        using var secondRecorder = CommandList.New(device);
        using var target = Texture.New2D(device, 64, 64, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        var values = new long[4];
        if (pool.TryGetData(values)) throw new Exception("Unwritten query results were available.");
        Reject<ArgumentException>(() => pool.TryGetData(new long[3]));
        Reject<ArgumentOutOfRangeException>(() => commands.WriteTimestamp(pool, -1));
        Reject<ArgumentOutOfRangeException>(() => commands.WriteTimestamp(pool, 4));
        long previousEnd = 0;
        for (var generation = 0; generation < 2; generation++)
        {
            commands.WriteTimestamp(pool, 0);
            Reject<InvalidOperationException>(() => secondRecorder.WriteTimestamp(pool, 1));
            Reject<InvalidOperationException>(() => commands.WriteTimestamp(pool, 0));
            if (pool.TryGetData(values)) throw new Exception("Recording queries were reported ready.");
            var first = commands.Close();
            if (pool.TryGetData(values)) throw new Exception("Unsubmitted queries were reported ready.");
            device.ExecuteCommandList(first); commands.Reset();
            device.WaitForGpuIdle();
            if (pool.TryGetData(values)) throw new Exception("Partially written pool was reported ready.");
            commands.WriteTimestamp(pool, 1);
            for (var i = 0; i < 16; i++) commands.Clear(target, i % 2 == 0 ? Color4.White : Color4.Black);
            commands.WriteTimestamp(pool, 2);
            commands.WriteTimestamp(pool, 3);
            var last = commands.Close();
            if (pool.TryGetData(values)) throw new Exception("Closed query recording was reported ready before submission.");
            device.ExecuteCommandList(last); commands.Reset();
            Read(pool, values);
            if (values[0] <= previousEnd || values[1] < values[0] || values[2] <= values[1] || values[3] < values[2])
                throw new Exception("Timestamp results were stale, zero or unordered: " + string.Join(", ", values));
            var seconds = (values[3] - values[0]) / (double)device.TimestampFrequency;
            if (seconds <= 0 || seconds > 30) throw new Exception($"Implausible timestamp interval {seconds} seconds at {device.TimestampFrequency} ticks/s: {string.Join(", ", values)}.");
            var copy = new long[4];
            if (!pool.TryGetData(copy) || !copy.SequenceEqual(values)) throw new Exception("Repeated timestamp read changed the result snapshot.");
            previousEnd = values[3];
        }
        using var cancelled = QueryPool.New(device, QueryType.Timestamp, 1);
        using (var abandoned = CommandList.New(device)) abandoned.WriteTimestamp(cancelled, 0);
        Reject<InvalidOperationException>(() => cancelled.TryGetData(new long[1]));
        commands.WriteTimestamp(cancelled, 0);
        device.ExecuteCommandList(commands.Close()); commands.Reset();
        Read(cancelled, new long[1]);
        cancelled.Dispose();
        Reject<ObjectDisposedException>(() => commands.WriteTimestamp(cancelled, 0));
        Console.WriteLine($"PASS: Asynchronous timestamp queries span submissions, preserve ordered values, reuse generations and reject invalid/abandoned recordings ({device.TimestampFrequency} ticks/s).");
    }

    private static void Read(QueryPool pool, long[] values)
    {
        var timer = Stopwatch.StartNew();
        while (!pool.TryGetData(values))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Timestamp readback did not complete.");
            Thread.Sleep(1);
        }
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
