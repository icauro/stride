// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

if (args.Length != 2) throw new ArgumentException("Usage: BindingsValidation --write-probe <probe.c> | --abi <probe-output> | --native <absolute-library-path> | --context <absolute-library-path> | --async-context <absolute-library-path>");
switch (args[0])
{
    case "--async-context": await AsyncContextValidation.Run(args[1]); break;
    case "--write-probe": LayoutValidation.WriteProbe(args[1]); break;
    case "--abi": LayoutValidation.Compare(args[1]); break;
    case "--native": NativeValidation.Run(args[1]); break;
    case "--spirv": ShaderValidation.Run(args[1]); break;
    case "--context":
        foreach (var backend in new[] { WGPUBackendType.Vulkan, WGPUBackendType.D3D12 })
        {
            using var context = Stride.Graphics.WebGpuDeviceContext.Create(Path.GetFullPath(args[1]), backend);
            context.WaitForIdle();
            if (context.Limits.maxTextureDimension2D == 0) throw new InvalidDataException("Missing device limits.");
            Console.WriteLine($"PASS: backend context on {context.BackendType}: {context.AdapterName}; maxTexture2D={context.Limits.maxTextureDimension2D}.");
            OffscreenValidation.Run(context);
            ContextValidation.CheckErrorAndDisposal(context);
        }
        break;
    default: throw new ArgumentException("Unknown validation mode.");
}

static unsafe class ContextValidation
{
    public static void CheckErrorAndDisposal(Stride.Graphics.WebGpuDeviceContext context)
    {
        var descriptor = WGPUBufferDescriptor.CreateDefault();
        descriptor.size = 16;
        descriptor.usage = WGPUBufferUsage.MapWrite | WGPUBufferUsage.Storage; // deliberately invalid portable usage
        var buffer = Api.wgpuDeviceCreateBuffer(context.Device, &descriptor);
        try
        {
            try { context.ThrowIfFailed(); throw new InvalidDataException("Expected a native buffer validation error."); }
            catch (InvalidOperationException e) when (e.Message.Contains("WebGPU Validation", StringComparison.Ordinal))
            { Console.WriteLine("PASS: native validation errors reach managed code without unwinding through a callback."); }
        }
        finally { if (buffer.Handle != 0) Api.wgpuBufferRelease(buffer); }
        context.WaitForIdle();
        // This pinned wgpu-native reports loss from failed API operations, not
        // directly from device_destroy. Probe the destroyed device explicitly.
        Api.wgpuDeviceDestroy(context.Device);
        descriptor.usage = WGPUBufferUsage.CopyDst;
        buffer = Api.wgpuDeviceCreateBuffer(context.Device, &descriptor);
        if (buffer.Handle != 0) Api.wgpuBufferRelease(buffer);
        try { context.ProcessEvents(); throw new InvalidDataException("Destroyed native device accepted buffer creation."); }
        catch (InvalidOperationException e) when (e.Message.Contains("device is lost", StringComparison.OrdinalIgnoreCase) || context.IsLost) { }
        Console.WriteLine("PASS: destroyed native device rejects resource creation. Physical device-loss callback delivery remains unverified.");
        context.Dispose();
        context.Dispose();
        if (context.Device.Handle != 0 || context.Instance.Handle != 0) throw new InvalidDataException("Disposed context retains native handles.");
        try { context.WaitForIdle(); throw new InvalidDataException("Disposed context accepted work."); }
        catch (ObjectDisposedException) { Console.WriteLine("PASS: disposal is idempotent and disposed contexts reject work."); }
    }
}

static class LayoutValidation
{
    private static Type[] Types => typeof(WGPUInstance).Assembly.GetTypes().Where(t => t.IsValueType && t.Namespace == "Stride.Graphics.WebGPU" && !t.IsEnum && t.Name.StartsWith("WGPU", StringComparison.Ordinal)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    private static string Key(Type type) => type.FullName!;
    private static string CType(Type type) => type.Name;
    private static IEnumerable<(string Path, Type Type, int Offset)> Leaves(Type type, string prefix = "", int offset = 0)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var path = prefix + field.Name;
            var fieldOffset = offset + Marshal.OffsetOf(type, field.Name).ToInt32();
            if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum && field.FieldType != typeof(nint) && field.FieldType != typeof(nuint) && !field.FieldType.GetFields().Any(f => f.Name == "Handle"))
                foreach (var leaf in Leaves(field.FieldType, path + ".", fieldOffset)) yield return leaf;
            else yield return (path, field.FieldType, fieldOffset);
        }
    }
    private static int Size(Type type) => type.IsPointer || type.IsFunctionPointer ? IntPtr.Size : Marshal.SizeOf(type.IsEnum ? Enum.GetUnderlyingType(type) : type);
    private static byte[] Bytes<T>(object value) where T : unmanaged
    { var typed = (T)value; return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref typed, 1)).ToArray(); }
    private static byte[] DefaultBytes(Type type) => (byte[])typeof(LayoutValidation).GetMethod(nameof(Bytes), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, [type.GetMethod("CreateDefault")!.Invoke(null, null)])!;
    private static Type[] Enums => typeof(WGPUInstance).Assembly.GetTypes().Where(t => t.IsEnum).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    public static void WriteProbe(string path)
    {
        var c = new StringBuilder("#include <stddef.h>\n#include <stdio.h>\n#include \"wgpu.h\"\nint main(void) {\n");
        foreach (var type in Types)
        {
            var native = CType(type);
            c.AppendLine($"printf(\"S|{Key(type)}|%zu\\n\", sizeof({native}));");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == "Handle") continue;
                c.AppendLine($"printf(\"F|{Key(type)}|{field.Name}|%zu\\n\", offsetof({native}, {field.Name}));");
            }
        }
        // Anonymous union gets a C# nested name; validate its containing member and size through the real C declaration.
        c.AppendLine("printf(\"U|%zu\\n\", sizeof(((WGPUNativeDisplayHandle*)0)->data));");
        foreach (var type in Enums)
            foreach (var name in Enum.GetNames(type))
            {
                var nativeName = name.StartsWith("WGPU", StringComparison.Ordinal) ? name : type.Name + "_" + name.TrimStart('_');
                c.AppendLine($"printf(\"E|{type.Name}|{name}|%llu\\n\", (unsigned long long){nativeName});");
            }
        var header = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../build/wgpu/headers/webgpu.h"))) + "\n" + File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../build/wgpu/headers/wgpu.h")));
        var macros = Regex.Matches(header, @"#define\s+(WGPU_\w+_INIT)\s+_wgpu_MAKE_INIT_STRUCT\((WGPU\w+),").ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);
        foreach (var type in Types.Where(t => t.GetMethod("CreateDefault") is not null))
        {
            c.AppendLine($"{type.Name} v_{type.Name} = {macros[type.Name]};");
            foreach (var leaf in Leaves(type))
            {
                var expression = $"v_{type.Name}.{leaf.Path}";
                c.AppendLine($"printf(\"D|{Key(type)}|{leaf.Path}|\"); for (size_t i=0; i<sizeof({expression}); i++) printf(\"%02x\", ((unsigned char*)&{expression})[i]); printf(\"\\n\");");
            }
        }
        c.AppendLine("return 0;\n}");
        File.WriteAllText(path, c.ToString());
    }
    public static void Compare(string path)
    {
        var byName = Types.ToDictionary(Key);
        var checks = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var values = line.Split('|');
            if (!seen.Add(string.Join('|', values[..^1]))) throw new InvalidDataException("Duplicate ABI probe entry.");
            if (values[0] == "D")
            {
                var type = byName[values[1]];
                var leaf = Leaves(type).Single(l => l.Path == values[2]);
                var expectedBytes = Convert.FromHexString(values[3]);
                var actualBytes = DefaultBytes(type).AsSpan(leaf.Offset, Size(leaf.Type));
                var nan = leaf.Type == typeof(float) && float.IsNaN(MemoryMarshal.Read<float>(expectedBytes)) && float.IsNaN(MemoryMarshal.Read<float>(actualBytes))
                    || leaf.Type == typeof(double) && double.IsNaN(MemoryMarshal.Read<double>(expectedBytes)) && double.IsNaN(MemoryMarshal.Read<double>(actualBytes));
                if (!nan && !actualBytes.SequenceEqual(expectedBytes)) throw new InvalidDataException($"Default mismatch: {line}; managed={Convert.ToHexString(actualBytes)}");
                checks++; continue;
            }
            if (values[0] == "E")
            {
                var type = Enums.Single(t => t.Name == values[1]);
                if (Convert.ToUInt64(Enum.Parse(type, values[2]), CultureInfo.InvariantCulture) != ulong.Parse(values[3], CultureInfo.InvariantCulture)) throw new InvalidDataException($"Enum mismatch: {line}");
                checks++; continue;
            }
            var expected = long.Parse(values[^1], CultureInfo.InvariantCulture);
            long actual = values[0] switch
            {
                "S" => Marshal.SizeOf(byName[values[1]]),
                "F" => Marshal.OffsetOf(byName[values[1]], values[2]).ToInt64(),
                "U" => Marshal.SizeOf(typeof(WGPUNativeDisplayHandle.data__Union)),
                _ => throw new InvalidDataException(line)
            };
            if (actual != expected) throw new InvalidDataException($"ABI mismatch: {line}; managed={actual}");
            checks++;
        }
        var requiredChecks = Types.Sum(t => 1 + t.GetFields(BindingFlags.Public | BindingFlags.Instance).Count(f => f.Name != "Handle")) + 1
            + Enums.Sum(t => Enum.GetNames(t).Length) + Types.Where(t => t.GetMethod("CreateDefault") is not null).Sum(t => Leaves(t).Count());
        if (checks != requiredChecks) throw new InvalidDataException($"Incomplete ABI probe: {checks}/{requiredChecks}");
        Console.WriteLine($"PASS: {checks} native layout, enum and descriptor-default comparisons across {Types.Length} structs/handles and {Enums.Length} enums.");
    }
}

static unsafe class NativeValidation
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterResult { public int Done; public WGPURequestAdapterStatus Status; public WGPUAdapter Adapter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceResult { public int Done; public WGPURequestDeviceStatus Status; public WGPUDevice Device; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MapResult { public int Done; public WGPUMapAsyncStatus Status; }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void AdapterCallback(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* userdata1, void* userdata2)
    { var result = (AdapterResult*)userdata1; result->Status = status; result->Adapter = adapter; Volatile.Write(ref result->Done, 1); }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void DeviceCallback(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* userdata1, void* userdata2)
    { var result = (DeviceResult*)userdata1; result->Status = status; result->Device = device; Volatile.Write(ref result->Done, 1); }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void MapCallback(WGPUMapAsyncStatus status, WGPUStringView message, void* userdata1, void* userdata2)
    { var result = (MapResult*)userdata1; result->Status = status; Volatile.Write(ref result->Done, 1); }
    private static void Wait(int* done, WGPUInstance instance, WGPUDevice device = default)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref *done) == 0)
        {
            Api.wgpuInstanceProcessEvents(instance);
            if (device.Handle != 0) Api.wgpuDevicePoll(device, 0, null);
            if (DateTime.UtcNow >= timeout) Environment.FailFast("WebGPU callback timed out; stopping before releasing callback storage.");
            Thread.Sleep(1);
        }
    }
    public static void Run(string path)
    {
        Api.LoadNativeLibrary(Path.GetFullPath(path));
        Console.WriteLine($"Native version: 0x{Api.wgpuGetVersion():x8}; all imported entry points resolved.");
        var instance = Api.wgpuCreateInstance(null);
        if (instance.Handle == 0) throw new InvalidOperationException("Instance creation failed.");
        WGPUAdapter adapter = default; WGPUDevice device = default; WGPUQueue queue = default;
        WGPUBuffer source = default; WGPUBuffer readback = default;
        try
        {
            AdapterResult adapterResult = default;
            var adapterInfo = WGPURequestAdapterCallbackInfo.CreateDefault();
            adapterInfo.mode = WGPUCallbackMode.AllowProcessEvents; adapterInfo.callback = &AdapterCallback; adapterInfo.userdata1 = &adapterResult;
            Api.wgpuInstanceRequestAdapter(instance, null, adapterInfo);
            Wait(&adapterResult.Done, instance); adapter = adapterResult.Adapter;
            if (adapterResult.Status != WGPURequestAdapterStatus.Success || adapter.Handle == 0) throw new InvalidOperationException($"Adapter request failed: {adapterResult.Status}");
            DeviceResult deviceResult = default;
            var deviceInfo = WGPURequestDeviceCallbackInfo.CreateDefault();
            deviceInfo.mode = WGPUCallbackMode.AllowProcessEvents; deviceInfo.callback = &DeviceCallback; deviceInfo.userdata1 = &deviceResult;
            Api.wgpuAdapterRequestDevice(adapter, null, deviceInfo);
            Wait(&deviceResult.Done, instance); device = deviceResult.Device;
            if (deviceResult.Status != WGPURequestDeviceStatus.Success || device.Handle == 0) throw new InvalidOperationException($"Device request failed: {deviceResult.Status}");
            queue = Api.wgpuDeviceGetQueue(device);
            var descriptor = WGPUBufferDescriptor.CreateDefault();
            descriptor.size = 16; descriptor.usage = WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst;
            source = Api.wgpuDeviceCreateBuffer(device, &descriptor);
            descriptor.usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst;
            readback = Api.wgpuDeviceCreateBuffer(device, &descriptor);
            uint* input = stackalloc uint[] { 0x12345678, 0xabcdef01, 42, uint.MaxValue };
            Api.wgpuQueueWriteBuffer(queue, source, 0, input, 16);
            var encoder = Api.wgpuDeviceCreateCommandEncoder(device, null);
            WGPUCommandBuffer commands = default;
            try
            {
                Api.wgpuCommandEncoderCopyBufferToBuffer(encoder, source, 0, readback, 0, 16);
                commands = Api.wgpuCommandEncoderFinish(encoder, null);
                Api.wgpuQueueSubmit(queue, 1, &commands);
            }
            finally
            {
                if (commands.Handle != 0) Api.wgpuCommandBufferRelease(commands);
                Api.wgpuCommandEncoderRelease(encoder);
            }
            MapResult mapResult = default;
            var mapInfo = WGPUBufferMapCallbackInfo.CreateDefault();
            mapInfo.mode = WGPUCallbackMode.AllowProcessEvents; mapInfo.callback = &MapCallback; mapInfo.userdata1 = &mapResult;
            Api.wgpuBufferMapAsync(readback, WGPUMapMode.Read, 0, 16, mapInfo);
            Wait(&mapResult.Done, instance, device);
            if (mapResult.Status != WGPUMapAsyncStatus.Success) throw new InvalidOperationException($"Buffer mapping failed: {mapResult.Status}");
            try
            {
                var result = (uint*)Api.wgpuBufferGetConstMappedRange(readback, 0, 16);
                if (result == null) throw new InvalidOperationException("Null mapped range.");
                for (var i = 0; i < 4; i++) if (result[i] != input[i]) throw new InvalidDataException($"GPU buffer mismatch at {i}");
            }
            finally { Api.wgpuBufferUnmap(readback); }
            Console.WriteLine("PASS: adapter/device callbacks, buffer upload, command submission, async mapping and exact GPU readback.");
        }
        finally
        {
            if (readback.Handle != 0) Api.wgpuBufferRelease(readback);
            if (source.Handle != 0) Api.wgpuBufferRelease(source);
            if (queue.Handle != 0) Api.wgpuQueueRelease(queue);
            if (device.Handle != 0) Api.wgpuDeviceRelease(device);
            if (adapter.Handle != 0) Api.wgpuAdapterRelease(adapter);
            Api.wgpuInstanceRelease(instance);
        }
    }
}
