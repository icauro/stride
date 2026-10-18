using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static void Main()
    {
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("This smoke app must run in a browser.");

        ReportStartup(RuntimeInformation.FrameworkDescription);
    }

    [JSImport("reportStartup", "smoke")]
    private static partial void ReportStartup(string framework);

    [JSExport]
    public static int Add(int left, int right) => checked(left + right);
}
