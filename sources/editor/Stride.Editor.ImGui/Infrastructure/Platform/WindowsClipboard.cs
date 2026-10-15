using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Context-owned ImGui clipboard callbacks; UTF-8 at ImGui and UTF-16 at Windows.</summary>
internal sealed unsafe class WindowsClipboard : IDisposable
{
    public static uint SequenceNumber => GetClipboardSequenceNumber();
    internal uint ChangeSequence => isolate ? cachedSequence : SequenceNumber;
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private const uint UnicodeText = 13;
    private readonly nint owner;
    private readonly bool isolate;
    private readonly PlatformGetClipboardTextFn getText;
    private readonly PlatformSetClipboardTextFn setText;
    private nint textBuffer;
    private uint cachedSequence;
    public WindowsClipboard(nint owner, bool isolate = false)
    {
        this.owner = owner; this.isolate = isolate; getText = GetText; setText = SetText;
        var platform = Gui.GetPlatformIO();
        platform.PlatformGetClipboardTextFn = (void*)Marshal.GetFunctionPointerForDelegate(getText);
        platform.PlatformSetClipboardTextFn = (void*)Marshal.GetFunctionPointerForDelegate(setText);
    }
    private byte* GetText(ImGuiContext* context)
    {
        string value = "";
        try
        {
            if (isolate)
            {
                textBuffer = textBuffer == 0 ? Marshal.StringToCoTaskMemUTF8("") : textBuffer;
                return (byte*)textBuffer;
            }
            var sequence = GetClipboardSequenceNumber();
            if (textBuffer != 0 && sequence != 0 && sequence == cachedSequence) return (byte*)textBuffer;
            if (!OpenClipboard(owner)) return null;
            try
            {
                var handle = GetClipboardData(UnicodeText);
                var bytes = handle == 0 ? 0 : GlobalSize(handle);
                if (handle != 0)
                {
                    if (bytes < 2 || bytes > (nuint)int.MaxValue) throw new InvalidDataException("The native Unicode clipboard size is invalid.");
                    var data = GlobalLock(handle);
                    if (data == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        var capacity = (int)(bytes / 2); var length = 0;
                        while (length < capacity && Marshal.ReadInt16(data, length * 2) != 0) length++;
                        if (length == capacity) throw new InvalidDataException("The native Unicode clipboard text is unterminated.");
                        value = Marshal.PtrToStringUni(data, length) ?? "";
                    }
                    finally { GlobalUnlock(handle); }
                }
                // Delayed native rendering may change the sequence while GetClipboardData runs.
                sequence = GetClipboardSequenceNumber();
            }
            finally { CloseClipboard(); }
            var nextBuffer = Marshal.StringToCoTaskMemUTF8(value);
            if (textBuffer != 0) Marshal.FreeCoTaskMem(textBuffer);
            textBuffer = nextBuffer;
            cachedSequence = sequence;
            return (byte*)textBuffer;
        }
        catch (Exception error) { Console.Error.WriteLine("Clipboard read: " + error.Message); return null; }
    }
    private void SetText(ImGuiContext* context, byte* text)
    {
        nint handle = 0;
        try
        {
            var value = text == null ? "" : Marshal.PtrToStringUTF8((nint)text) ?? "";
            if (isolate)
            {
                // Synthetic workflows retain the same native UTF-8 callback
                // contract without reading or writing the user's OS clipboard.
                var nextBuffer = Marshal.StringToCoTaskMemUTF8(value);
                if (textBuffer != 0) Marshal.FreeCoTaskMem(textBuffer);
                textBuffer = nextBuffer;
                unchecked { cachedSequence++; }
                return;
            }
            var bytes = Encoding.Unicode.GetBytes(value + '\0');
            if (!OpenClipboard(owner)) return;
            try
            {
                handle = GlobalAlloc(0x0002, (nuint)bytes.Length);
                if (handle == 0) return;
                var data = GlobalLock(handle);
                if (data == 0) return;
                try { Marshal.Copy(bytes, 0, data, bytes.Length); }
                finally { GlobalUnlock(handle); }
                if (EmptyClipboard() && SetClipboardData(UnicodeText, handle) != 0) handle = 0; // Windows now owns this allocation.
            }
            finally { CloseClipboard(); }
        }
        catch (Exception error) { Console.Error.WriteLine("Clipboard write: " + error.Message); }
        finally { if (handle != 0) GlobalFree(handle); }
    }
    public void Dispose()
    {
        var platform = Gui.GetPlatformIO();
        platform.PlatformGetClipboardTextFn = null; platform.PlatformSetClipboardTextFn = null;
        if (textBuffer != 0) { Marshal.FreeCoTaskMem(textBuffer); textBuffer = 0; }
        GC.KeepAlive(getText); GC.KeepAlive(setText);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
}
