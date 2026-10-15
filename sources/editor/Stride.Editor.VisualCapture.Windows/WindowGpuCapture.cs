using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;
using Windows.Graphics.Capture;
using WinRT;
using WinDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace Stride.Editor.VisualCapture.Windows;

/// <summary>Optional in-process DWM window texture capture; independent of Stride's graphics API.</summary>
public static class WindowGpuCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ItemInterop
    {
        [PreserveSig] int CreateForWindow(nint hwnd, ref Guid iid, out nint value);
        [PreserveSig] int CreateForMonitor(nint monitor, ref Guid iid, out nint value);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface SurfaceInterop { [PreserveSig] int GetInterface(ref Guid iid, out nint value); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int GetCloaked(nint hwnd, uint attribute, out int value, int size);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
    [DllImport("combase.dll", PreserveSig = false)] private static extern void WindowsCreateString([MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value, uint length, out nint hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll", PreserveSig = false)] private static extern void RoGetActivationFactory(nint name, in Guid iid, out nint factory);

    private static ItemInterop Factory()
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(name, (uint)name.Length, out var value);
        try
        {
            RoGetActivationFactory(value, typeof(ItemInterop).GUID, out var factory);
            try { return (ItemInterop)Marshal.GetObjectForIUnknown(factory); }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(value); }
    }

    private static Rect Bounds(nint hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) throw new InvalidOperationException("The capture window is hidden, minimized or closed.");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("The window does not belong to this editor process.");
        if (!GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != 0) throw new InvalidOperationException("The window's capture affinity excludes capture or cannot be verified.");
        Marshal.ThrowExceptionForHR(GetCloaked(hwnd, 14, out var cloaked, sizeof(int)));
        if (cloaked != 0) throw new InvalidOperationException("The capture window is cloaked.");
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 9, out var bounds, Marshal.SizeOf<Rect>()));
        return bounds;
    }

    // Task<string> keeps WinRT projection types out of the ordinary WPF host's reference graph.
    public static async Task<string> CaptureAsync(nint hwnd, int x, int y, int width, int height, string output, int timeoutMs, CancellationToken cancellation)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this system.");
        hwnd = GetAncestor(hwnd, 2);
        var before = Bounds(hwnd);
        var crop = new Rectangle(x - before.Left, y - before.Top, width, height);
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000 || crop.Left < 0 || crop.Top < 0
            || crop.Right > before.Right - before.Left || crop.Bottom > before.Bottom - before.Top)
            throw new InvalidOperationException("The requested physical rectangle is outside the captured window.");
        using var resources = new Resources(hwnd);
        var done = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object(); var closing = false;
        global::Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> arrived = (pool, _) =>
        {
            lock (gate)
            {
                if (closing) return;
                try
                {
                    var frame = pool.TryGetNextFrame();
                    if (frame != null && !done.TrySetResult(frame)) frame.Dispose();
                }
                catch (Exception error) { done.TrySetException(error); }
            }
        };
        var temporary = output + ".wgc-" + Guid.NewGuid().ToString("N");
        resources.Pool.FrameArrived += arrived;
        Direct3D11CaptureFrame? owned = null;
        try
        {
            resources.Session.StartCapture();
            owned = await done.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 100, 120000)), cancellation).ConfigureAwait(false);
            var size = owned.ContentSize;
            var frameBounds = Bounds(hwnd);
            if (frameBounds.Left != before.Left || frameBounds.Top != before.Top || frameBounds.Right != before.Right || frameBounds.Bottom != before.Bottom
                || size.Width != before.Right - before.Left || size.Height != before.Bottom - before.Top)
                throw new InvalidOperationException("Window geometry or WGC content size changed; retry capture after layout settles.");
            Encode(owned, resources.Device, resources.Context, crop, temporary);
            var after = Bounds(hwnd);
            if (!before.Equals(after)) throw new InvalidOperationException("The capture window moved or resized during GPU readback.");
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, output, true);
            return JsonSerializer.Serialize(new
            {
                composition = "dwm-window-gpu-texture-readback", pixelBackend = "windows-graphics-capture-d3d11",
                captureHwnd = "0x" + hwnd.ToInt64().ToString("X"), cursorIncluded = resources.CursorIncluded,
                windowRect = new { x = before.Left, y = before.Top, width = size.Width, height = size.Height },
                textureCrop = new { x = crop.X, y = crop.Y, width, height },
                contentSize = new { width = size.Width, height = size.Height },
                systemRelativeTimeTicks = owned.SystemRelativeTime.Ticks,
                occlusion = new { required = false, method = "window-compositor-texture-independent-of-other-desktop-windows" },
                engineFramesVerified = false
            });
        }
        finally
        {
            lock (gate) { closing = true; resources.Pool.FrameArrived -= arrived; }
            if (owned != null) owned.Dispose();
            else if (done.Task.IsCompletedSuccessfully) done.Task.Result.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed unsafe class Resources : IDisposable
    {
        internal nint Device, Context;
        internal Direct3D11CaptureFramePool Pool = null!;
        internal GraphicsCaptureSession Session = null!;
        internal bool CursorIncluded = true;
        private WinDevice? projected;
        private D3D11? api;
        internal Resources(nint hwnd)
        {
            try
            {
                var factory = Factory();
                GraphicsCaptureItem item;
                try
                {
                    var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                    Marshal.ThrowExceptionForHR(factory.CreateForWindow(hwnd, ref iid, out var abi));
                    try { item = MarshalInterface<GraphicsCaptureItem>.FromAbi(abi); }
                    finally { Marshal.Release(abi); }
                }
                finally { Marshal.ReleaseComObject(factory); }
                api = D3D11.GetApi(null);
                ID3D11Device* device = null; ID3D11DeviceContext* context = null; D3DFeatureLevel level = 0;
                var result = api.CreateDevice(null, D3DDriverType.Hardware, 0, (uint)CreateDeviceFlag.BgraSupport,
                    null, 0, D3D11.SdkVersion, ref device, &level, ref context);
                Device = (nint)device; Context = (nint)context;
                SilkMarshal.ThrowHResult(result);
                IDXGIDevice* dxgi = null; var dxgiIid = IDXGIDevice.Guid;
                SilkMarshal.ThrowHResult(device->QueryInterface(ref dxgiIid, (void**)&dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice((nint)dxgi, out var abi));
                    try { projected = MarshalInspectable<WinDevice>.FromAbi(abi); }
                    finally { Marshal.Release(abi); }
                }
                finally { dxgi->Release(); }
                if (item.Size.Width <= 0 || item.Size.Height <= 0) throw new InvalidOperationException("The capture window has no realized content.");
                Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(projected,
                    global::Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                Session = Pool.CreateCaptureSession(item);
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                { Session.IsCursorCaptureEnabled = false; CursorIncluded = false; }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            Session?.Dispose(); Pool?.Dispose(); projected?.Dispose();
            if (Context != 0) { ((IUnknown*)Context)->Release(); Context = 0; }
            if (Device != 0) { ((IUnknown*)Device)->Release(); Device = 0; }
            api?.Dispose();
        }
    }

    private static unsafe void Encode(Direct3D11CaptureFrame frame, nint deviceAbi, nint contextAbi, Rectangle crop, string path)
    {
        var iid = ID3D11Texture2D.Guid;
        Marshal.ThrowExceptionForHR(frame.Surface.As<SurfaceInterop>().GetInterface(ref iid, out var abi));
        var source = (ID3D11Texture2D*)abi;
        try
        {
            Texture2DDesc description; source->GetDesc(&description);
            if (description.Width < frame.ContentSize.Width || description.Height < frame.ContentSize.Height)
                throw new InvalidOperationException("The GPU texture is smaller than the compositor frame.");
            description.Width = (uint)crop.Width; description.Height = (uint)crop.Height;
            description.Usage = Usage.Staging; description.CPUAccessFlags = (uint)CpuAccessFlag.Read;
            description.BindFlags = 0; description.MiscFlags = 0;
            ID3D11Texture2D* staging = null;
            var device = (ID3D11Device*)deviceAbi; var context = (ID3D11DeviceContext*)contextAbi;
            SilkMarshal.ThrowHResult(device->CreateTexture2D(in description, null, ref staging));
            try
            {
                var region = new Box((uint)crop.Left, (uint)crop.Top, 0, (uint)crop.Right, (uint)crop.Bottom, 1);
                context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &region);
                MappedSubresource mapped = default;
                SilkMarshal.ThrowHResult(context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, ref mapped));
                try
                {
                    using var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
                    var bits = bitmap.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (var row = 0; row < crop.Height; row++)
                            Buffer.MemoryCopy((byte*)mapped.PData + row * mapped.RowPitch, (byte*)bits.Scan0 + row * bits.Stride, bits.Stride, crop.Width * 4);
                    }
                    finally { bitmap.UnlockBits(bits); }
                    bitmap.Save(path, ImageFormat.Png);
                }
                finally { context->Unmap((ID3D11Resource*)staging, 0); }
            }
            finally { staging->Release(); }
        }
        finally { source->Release(); }
    }
}
