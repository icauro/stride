using Stride.Core.Yaml.Events;
using Xunit;

namespace Stride.Core.Yaml.Tests;

public class TestUnicodeScalarRoundtrip
{
    [Theory]
    [InlineData("Ω 日本語 🚀", "\\U0001F680")]
    [InlineData("\U00010000\U0010FFFF", "\\U0010FFFF")]
    [InlineData("\uFFFE", "\\uFFFE")]
    [InlineData("\u0001", "\\x01")]
    public void QuotedUnicodeRoundtrip(string value, string expectedEscape)
    {
        using var output = new StringWriter();
        var emitter = new Emitter(output);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));
        emitter.Emit(new Scalar(null, null, value, ScalarStyle.DoubleQuoted, false, true));
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        var yaml = output.ToString();
        Assert.Contains(expectedEscape, yaml);
        var parser = new Parser(new StringReader(yaml));
        string? actual = null;
        while (parser.MoveNext())
            if (parser.Current is Scalar scalar) actual = scalar.Value;
        Assert.Equal(value, actual);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDFFF")]
    [InlineData("\\U0000D800")]
    [InlineData("\\U00110000")]
    public void InvalidUnicodeScalarStillRejected(string escape)
    {
        var parser = new Parser(new StringReader("\"" + escape + "\""));
        Assert.Throws<SyntaxErrorException>(() => { while (parser.MoveNext()) { } });
    }
}
