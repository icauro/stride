using System.Security.Cryptography;
using System.Text;

namespace Stride.Editor.ImGui;

internal static class PollingCertificateByteVerification
{
    internal static object Verify()
    {
        var cases = new (string Name, string?[] Fields)[]
        {
            ("no-fields", []),
            ("null-empty", [null, "", null, "", "value"]),
            ("unicode-and-surrogates", ["Ω 日本語 😀", "\ud800", "\udc00", "x\ud800y\udc00z", "\ud83d\ude80", "e\u0301"]),
            ("exact-16k-payload-end", [new string('a', 16380), null, "", "next"]),
            ("prefix-flush-after-three-bytes-remain", [new string('a', 16377), "next", null]),
            ("payload-flush-after-prefix", [new string('a', 16381), "next", ""]),
            ("16k-field", [new string('a', 16384), "next"]),
            ("oversized-ascii-field", ["before", new string('a', 32769), "after", null]),
            ("oversized-utf8-field", ["before", new string('Ω', 17000), "after", "😀"]),
            ("many-buffer-crossings", Enumerable.Range(0, 12000).Select(index => (index % 5) switch
                { 0 => null, 1 => "", 2 => "asset/path/Ω", 3 => "😀", _ => "ordinary-value" }).ToArray())
        };
        foreach (var item in cases)
        {
            var expected = Reference(item.Fields);
            for (var repeat = 0; repeat < 3; repeat++)
                if (AuthoredImportPollingCache.ComputeCertificate(item.Fields) != expected)
                    throw new InvalidOperationException("Buffered native polling certificate changed exact framed bytes: " + item.Name);
        }
        var distinctions = new (string?[] Left, string?[] Right)[]
        {
            (["ab", "c"], ["a", "bc"]), ([null], [""]), ([], [""]),
            (["first", "second"], ["second", "first"])
        };
        foreach (var pair in distinctions)
            if (AuthoredImportPollingCache.ComputeCertificate(pair.Left) == AuthoredImportPollingCache.ComputeCertificate(pair.Right))
                throw new InvalidOperationException("Polling certificate lost field framing, null identity or field order.");
        return new { cases = cases.Select(item => item.Name).ToArray(), repetitions = 3,
            distinctionCases = distinctions.Length, exactReferenceBytesMatched = true,
            boundary = "Independent full byte-stream SHA256 reference; exercises production pooled buffering through ComputeCertificate. No assumption about the rented buffer's exact capacity." };
    }
    private static string Reference(IReadOnlyList<string?> fields)
    {
        using var bytes = new MemoryStream();
        foreach (var field in fields)
        {
            var payload = field == null ? null : Encoding.UTF8.GetBytes(field);
            var length = payload?.Length ?? -1;
            // Independent explicit signed 32-bit little-endian framing, avoiding
            // production InputCertificate/BinaryPrimitives/buffer-flush logic.
            bytes.WriteByte(unchecked((byte)length));
            bytes.WriteByte(unchecked((byte)(length >> 8)));
            bytes.WriteByte(unchecked((byte)(length >> 16)));
            bytes.WriteByte(unchecked((byte)(length >> 24)));
            if (payload != null) bytes.Write(payload);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
