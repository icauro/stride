using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Stride.Editor.ImGui;

/// <summary>STA OLE text transfer sharing native window ownership with the existing file-drop queue.</summary>
internal static class WindowsSourceTextDragDrop
{
    internal const uint Copy = 1, Move = 2;
    internal sealed record Data(string Text, bool Rectangle, byte[]? Html = null, object? Origin = null);
    private static readonly Dictionary<nint, WindowTarget> windows = [];
    private static readonly short Counted = unchecked((short)RegisterClipboardFormatW("Stride.Editor.SourceText.v1"));
    private static readonly short Rectangle = unchecked((short)RegisterClipboardFormatW("AvalonEditRectangularSelection"));
    private static readonly short Html = unchecked((short)RegisterClipboardFormatW("HTML Format"));
    private static nint activeIdentity;
    private static Data? activeData;
    private static Exception? callbackError;
    private static bool dragging;
    private static bool pumpingFrame;
    private static long lastFrame;
    private static nuint nextTimer = 0x53524344;
    internal static int Frame { get; set; }
    internal static bool IsDragging => dragging || windows.Values.Any(window => window.HasIncomingText);
    internal static void ObserveFailure()
    {
        var error = callbackError; callbackError = null;
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    internal static bool ExceedsThreshold(Vector2 delta) => Math.Abs(delta.X) > GetSystemMetrics(68) || Math.Abs(delta.Y) > GetSystemMetrics(69);
    internal static bool IsBelowThreshold(Vector2 delta) => Math.Abs(delta.X) < GetSystemMetrics(68) && Math.Abs(delta.Y) < GetSystemMetrics(69);

    internal sealed class Binding : IDisposable
    {
        private WindowTarget? owner;
        internal Func<Data, Vector2, uint, uint, bool, uint>? Transfer;
        internal int SeenFrame = -1;
        internal Action? ModalTick, ClearHover;
        internal Binding(WindowTarget owner) { this.owner = owner; owner.Bindings.Add(this); }
        public void Dispose()
        {
            var window = owner; if (window == null) return;
            if (window.Bindings.Count == 1) { window.Dispose(); windows.Remove(window.Window); }
            owner = null; Transfer = null; ModalTick = null; ClearHover = null; window.Bindings.Remove(this);
        }
    }
    internal static Binding Attach(nint window)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Native source transfer requires the editor's STA owner thread.");
        if (!windows.TryGetValue(window, out var owner)) windows.Add(window, owner = new(window));
        return new(owner);
    }
    internal static Vector2 ClientOrigin(nint window)
    {
        var point = new WindowsSourceDropPoint();
        if (!ClientToScreen(window, ref point)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y);
    }
    internal static uint Start(nint window, Data data, uint effects)
    {
        if (dragging) throw new InvalidOperationException("A native source drag is already active.");
        if (!windows.ContainsKey(window)) throw new InvalidOperationException("The native source window is no longer registered.");
        var source = new SourceObject(data); var origin = new DropSource();
        activeIdentity = Marshal.GetIUnknownForObject(source); activeData = data; callbackError = null; dragging = true;
        try
        {
            using var modal = windows[window].AcquireModalTimer();
            ReleaseCapture();
            var result = DoDragDrop(source, origin, effects, out var effect);
            ObserveFailure();
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            if (result == 0x40100 && effect == Move && data.Text.Contains('\0') && !source.CountedDelivered)
                throw new InvalidDataException("The destination did not accept the complete length-aware source. The original source is retained.");
            return result == 0x40100 ? effect : 0; // DRAGDROP_S_DROP; cancellation is not a completed transfer.
        }
        finally { dragging = false; activeData = null; Marshal.Release(activeIdentity); activeIdentity = 0; GC.KeepAlive(source); GC.KeepAlive(origin); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class WindowTarget : IWindowsSourceDropTarget, IDisposable
    {
        internal readonly nint Window;
        internal readonly List<Binding> Bindings = [];
        private IDataObject? incoming;
        private Data? text;
        private bool files, initialized, registered;
        private bool disposed;
        private readonly TimerProcedure timerProcedure;
        private readonly nuint timerId = ++nextTimer;
        private int timerUsers;
        private IDisposable? incomingTimer;
        private WindowsSourceDropPoint lastPoint;
        private uint lastKeys, lastAllowed;
        internal bool HasIncomingText => incoming != null && text != null;
        internal WindowTarget(nint window)
        {
            if (window == 0) throw new ArgumentException("A native source window is required.", nameof(window));
            Window = window;
            timerProcedure = OnTimer;
            var result = OleInitialize(0); if (result < 0) Marshal.ThrowExceptionForHR(result); initialized = true;
            result = RegisterDragDrop(window, this);
            if (result < 0) { OleUninitialize(); initialized = false; Marshal.ThrowExceptionForHR(result); }
            registered = true;
        }
        public int DragEnter(IDataObject value, uint keys, WindowsSourceDropPoint point, ref uint effect)
        {
            incoming = value; text = null;
            try { incomingTimer?.Dispose(); incomingTimer = null; files = Has(value, 15); if (!files) text = Read(value); if (text != null) incomingTimer = AcquireModalTimer(); effect = Evaluate(keys, point, effect, false); }
            catch (Exception error) { callbackError ??= error; effect = 0; }
            return 0;
        }
        public int DragOver(uint keys, WindowsSourceDropPoint point, ref uint effect)
        {
            try { effect = Evaluate(keys, point, effect, false); }
            catch (Exception error) { callbackError ??= error; effect = 0; }
            return 0;
        }
        public int DragLeave() { incoming = null; text = null; files = false; incomingTimer?.Dispose(); incomingTimer = null; foreach (var binding in Bindings.ToArray()) binding.ClearHover?.Invoke(); return 0; }
        public int Drop(IDataObject value, uint keys, WindowsSourceDropPoint point, ref uint effect)
        {
            try
            {
                incoming = value;
                if (files)
                {
                    var format = Format(15); value.GetData(ref format, out var medium);
                    try { effect = medium.tymed == TYMED.TYMED_HGLOBAL && WindowsFileDrop.DispatchOleFiles(Window, medium.unionmember, point.X, point.Y) ? Copy & effect : 0; }
                    finally { ReleaseStgMedium(ref medium); }
                }
                else { text = Read(value); effect = Evaluate(keys, point, effect, true); }
            }
            catch (Exception error) { callbackError ??= error; effect = 0; }
            finally { DragLeave(); }
            return 0;
        }
        private uint Evaluate(uint keys, WindowsSourceDropPoint point, uint allowed, bool drop)
        {
            lastPoint = point; lastKeys = keys; lastAllowed = allowed;
            foreach (var binding in Bindings.ToArray()) binding.ClearHover?.Invoke();
            if (files) return WindowsFileDrop.AcceptsOleFiles(Window) ? allowed & Copy : 0;
            if (text == null) return 0;
            foreach (var binding in Bindings.ToArray().Reverse())
            {
                if (binding.SeenFrame != Frame) continue;
                var effect = binding.Transfer?.Invoke(text, new(point.X, point.Y), keys, allowed, drop) ?? 0;
                if (effect != 0) return effect & allowed;
            }
            return 0;
        }
        internal IDisposable AcquireModalTimer()
        {
            if (disposed) throw new ObjectDisposedException(nameof(WindowTarget));
            if (timerUsers++ == 0 && SetTimer(Window, timerId, 16, timerProcedure) == 0)
            { timerUsers--; throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            return new TimerLease(this);
        }
        private sealed class TimerLease(WindowTarget owner) : IDisposable
        {
            private WindowTarget? target = owner;
            public void Dispose() { var value = target; target = null; if (value != null && --value.timerUsers == 0) KillTimer(value.Window, value.timerId); }
        }
        private void OnTimer(nint window, uint message, nuint id, uint time)
        {
            if (disposed || pumpingFrame || callbackError != null || !HasIncomingText || Environment.TickCount64 - lastFrame < 16) return;
            try
            {
                // Re-hit actual last-rendered geometry, then let the host finish a real native frame.
                if (Evaluate(lastKeys, lastPoint, lastAllowed, false) == 0) return;
                var binding = Bindings.FirstOrDefault(value => value.SeenFrame == Frame && value.ModalTick != null);
                if (binding == null) return;
                pumpingFrame = true; lastFrame = Environment.TickCount64; binding.ModalTick!();
            }
            catch (Exception error) { callbackError ??= error; }
            finally { pumpingFrame = false; }
        }
        public void Dispose()
        {
            if (disposed) return; KillTimer(Window, timerId);
            DragLeave();
            if (registered) { if (IsWindow(Window)) Marshal.ThrowExceptionForHR(RevokeDragDrop(Window)); registered = false; }
            if (initialized) { OleUninitialize(); initialized = false; }
            disposed = true;
            GC.KeepAlive(this);
        }
    }
    private static Data? Read(IDataObject value)
    {
        var format = Has(value, Counted) ? Counted : (short)13;
        if (!Has(value, format)) return null;
        var descriptor = Format(format); value.GetData(ref descriptor, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL) return null;
            var size = GlobalSize(medium.unionmember); if (size < 2 || size > (nuint)int.MaxValue) return null;
            var pointer = GlobalLock(medium.unionmember); if (pointer == 0) return null;
            try
            {
                string contents;
                if (format == Counted)
                {
                    if (size < 4) return null;
                    var length = Marshal.ReadInt32(pointer); if (length < 0 || (nuint)length > (size - 4) / 2) return null;
                    contents = Marshal.PtrToStringUni(pointer + 4, length) ?? "";
                }
                else
                {
                    var capacity = (int)(size / 2); var length = 0;
                    while (length < capacity && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
                    if (length == capacity) return null;
                    contents = Marshal.PtrToStringUni(pointer, length) ?? "";
                }
                object? origin = null;
                if (activeIdentity != 0)
                {
                    var identity = Marshal.GetIUnknownForObject(value);
                    try { if (identity == activeIdentity) origin = activeData?.Origin; }
                    finally { Marshal.Release(identity); }
                }
                return new(contents, Has(value, Rectangle), Origin: origin);
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }
    private static bool Has(IDataObject data, short id) { if (id == 0) return false; var format = Format(id); return data.QueryGetData(ref format) == 0; }
    private static FORMATETC Format(short id) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SourceObject : IDataObject
    {
        private readonly Dictionary<short, byte[]> formats = [];
        internal bool CountedDelivered { get; private set; }
        internal SourceObject(Data data)
        {
            formats.Add(13, Encoding.Unicode.GetBytes(data.Text + '\0'));
            var raw = new byte[checked(4 + data.Text.Length * 2)]; BitConverter.GetBytes(data.Text.Length).CopyTo(raw, 0); Encoding.Unicode.GetBytes(data.Text).CopyTo(raw, 4);
            if (Counted != 0) formats.Add(Counted, raw);
            if (data.Rectangle && Rectangle != 0) formats.Add(Rectangle, [1]);
            if (data.Html != null && Html != 0) formats.Add(Html, data.Html);
        }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            var result = QueryGetData(ref format); if (result != 0) Marshal.ThrowExceptionForHR(result);
            var bytes = formats[format.cfFormat]; var handle = GlobalAlloc(2, (nuint)bytes.Length);
            if (handle == 0) throw new OutOfMemoryException();
            var pointer = GlobalLock(handle);
            if (pointer == 0) { GlobalFree(handle); throw new OutOfMemoryException(); }
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            catch { GlobalUnlock(handle); GlobalFree(handle); throw; }
            GlobalUnlock(handle); medium = new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
            if (format.cfFormat == Counted) CountedDelivered = true;
        }
        public int QueryGetData(ref FORMATETC format) => format.dwAspect == DVASPECT.DVASPECT_CONTENT && format.lindex == -1 && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 && formats.ContainsKey(format.cfFormat) ? 0 : unchecked((int)0x80040064);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Unsupported storage medium.", unchecked((int)0x80040064));
        public int GetCanonicalFormatEtc(ref FORMATETC format, out FORMATETC canonical) { canonical = format; canonical.ptd = 0; return 0x40130; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new COMException("Source data is immutable.", unchecked((int)0x80040064));
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET ? new FormatEnumerator(formats.Keys.Select(Format).ToArray()) : throw new COMException("Source data is immutable.", unchecked((int)0x80040064));
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new COMException("Advisory connections are unavailable.", unchecked((int)0x80040003));
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return unchecked((int)0x80040003); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int position;
        public int Next(int count, FORMATETC[] output, int[]? fetched)
        {
            if (count < 0) return unchecked((int)0x80070057);
            var copied = 0; while (copied < count && copied < output.Length && position < formats.Length) output[copied++] = formats[position++];
            if (fetched is { Length: > 0 }) fetched[0] = copied;
            return copied == count ? 0 : 1;
        }
        public int Skip(int count) { if (count < 0) return unchecked((int)0x80070057); var available = formats.Length - position; position += Math.Min(count, available); return count <= available ? 0 : 1; }
        public int Reset() { position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) { var result = new FormatEnumerator(formats) { position = position }; clone = result; }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource : IWindowsSourceDropSource
    {
        public int QueryContinueDrag(bool escape, uint keys) => escape ? 0x40101 : (keys & 1) == 0 ? 0x40100 : 0;
        public int GiveFeedback(uint effect) => 0x40102;
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, IWindowsSourceDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
    [DllImport("ole32.dll")] private static extern int DoDragDrop(IDataObject data, IWindowsSourceDropSource source, uint allowed, out uint effect);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TimerProcedure(nint window, uint message, nuint id, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint interval, TimerProcedure procedure);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref WindowsSourceDropPoint point);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowsSourceDropPoint { public int X, Y; }
[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsSourceDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, WindowsSourceDropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, WindowsSourceDropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, WindowsSourceDropPoint point, ref uint effect);
}
[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsSourceDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    [PreserveSig] int GiveFeedback(uint effect);
}
