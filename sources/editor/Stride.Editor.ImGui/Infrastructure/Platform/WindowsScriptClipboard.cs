using System.Runtime.InteropServices;
using System.Text;

namespace Stride.Editor.ImGui;

/// <summary>Actual Unicode/whole-line clipboard formats used by the reference source editor.</summary>
internal static class WindowsScriptClipboard
{
    private const uint UnicodeText = 13;
    private static readonly uint LineSelected = RegisterClipboardFormatW("MSDEVLineSelect");
    private static readonly uint HtmlFormat = RegisterClipboardFormatW("HTML Format");
    private static readonly uint RectangleFormat = RegisterClipboardFormatW("AvalonEditRectangularSelection");
    private static readonly uint LengthAwareText = RegisterClipboardFormatW("Stride.Editor.SourceText.v1");
    internal static bool TryWrite(nint owner, string text, bool wholeLine, byte[]? html = null, bool rectangle = false)
    {
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var rawBytes = new byte[checked(text.Length * 2 + 4)];
        BitConverter.GetBytes(text.Length).CopyTo(rawBytes, 0); Encoding.Unicode.GetBytes(text).CopyTo(rawBytes, 4);
        var content = Allocate(bytes); var marker = wholeLine ? Allocate([1]) : 0;
        var richText = html == null ? 0 : Allocate(html);
        var raw = Allocate(rawBytes); var rectangleMarker = rectangle ? Allocate([1]) : 0;
        if (content == 0 || raw == 0 || wholeLine && marker == 0 || html != null && richText == 0 || rectangle && rectangleMarker == 0)
        { Free(content); Free(marker); Free(richText); Free(raw); Free(rectangleMarker); return false; }
        try
        {
            if (!OpenClipboard(owner)) return false;
            try
            {
                if (!EmptyClipboard() || SetClipboardData(UnicodeText, content) == 0) return false;
                content = 0;
                if (LengthAwareText == 0 || SetClipboardData(LengthAwareText, raw) == 0) return false;
                raw = 0;
                if (rectangle)
                {
                    if (RectangleFormat == 0 || SetClipboardData(RectangleFormat, rectangleMarker) == 0) return false;
                    rectangleMarker = 0;
                }
                if (richText != 0)
                {
                    if (HtmlFormat == 0 || SetClipboardData(HtmlFormat, richText) == 0) return false;
                    richText = 0;
                }
                if (wholeLine)
                {
                    if (LineSelected == 0 || SetClipboardData(LineSelected, marker) == 0) return false;
                    marker = 0;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally { Free(content); Free(marker); Free(richText); Free(raw); Free(rectangleMarker); }
    }
    internal static (string Text, bool WholeLine, bool Rectangle)? Read(nint owner)
    {
        if (!OpenClipboard(owner)) return null;
        try
        {
            var raw = LengthAwareText == 0 ? 0 : GetClipboardData(LengthAwareText);
            var handle = raw != 0 ? raw : GetClipboardData(UnicodeText);
            if (handle == 0) return null;
            var pointer = GlobalLock(handle);
            if (pointer == 0) return null;
            try
            {
                var bytes = GlobalSize(handle);
                if (bytes > (nuint)int.MaxValue || bytes < 2) return null;
                if (raw != 0)
                {
                    if (bytes < 4) return null;
                    var rawLength = Marshal.ReadInt32(pointer);
                    if (rawLength < 0 || (nuint)rawLength > (bytes - 4) / 2) return null;
                    return (Marshal.PtrToStringUni(pointer + 4, rawLength) ?? "", LineSelected != 0 && IsClipboardFormatAvailable(LineSelected),
                        RectangleFormat != 0 && IsClipboardFormatAvailable(RectangleFormat));
                }
                var count = (int)(bytes / 2); var length = 0;
                while (length < count && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
                if (length == count) return null;
                return (Marshal.PtrToStringUni(pointer, length) ?? "", LineSelected != 0 && IsClipboardFormatAvailable(LineSelected),
                    RectangleFormat != 0 && IsClipboardFormatAvailable(RectangleFormat));
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    private static nint Allocate(byte[] bytes)
    {
        var handle = GlobalAlloc(2, (nuint)bytes.Length);
        if (handle == 0) return 0;
        var pointer = GlobalLock(handle);
        if (pointer == 0) { Free(handle); return 0; }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }
    private static void Free(nint handle) { if (handle != 0) GlobalFree(handle); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
}
