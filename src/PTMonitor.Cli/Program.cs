using System.Text;
using PTMonitor.Core.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;

const string AppName = "PTMonitor.Cli";
const string Version = "1.0.0";

var arguments = args;

if (arguments.Contains("--selftest"))
{
    var result = await SelfTest.RunAsync();
    if (result.IsSuccess)
    {
        Console.Out.WriteLine($"selftest: OK ({result.Passed} checks passed)");
        return 0;
    }
    Console.Error.WriteLine($"selftest: FAILED — {result.Failure}");
    return 1;
}

if (arguments.Contains("--probe"))
    return await ProbeRunner.RunAsync(arguments);

if (arguments.Contains("--help") || arguments.Contains("-h") || arguments.Length == 0)
{
    Console.Out.WriteLine($"{AppName} {Version}");
    Console.Out.WriteLine();
    Console.Out.WriteLine("Usage:");
    Console.Out.WriteLine("  PTMonitor.Cli --selftest          Run the built-in checks and exit 0/1");
    Console.Out.WriteLine("  PTMonitor.Cli --probe <type> ...  Fetch one server snapshot as JSON");
    Console.Out.WriteLine();
    Console.Out.WriteLine(ProbeRunner.Usage);
    return arguments.Length == 0 ? 2 : 0;
}

Console.Error.WriteLine($"{AppName}: unexpected argument(s) {string.Join(" ", arguments)}");
return 2;
