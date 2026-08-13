using BenchmarkDotNet.Running;

namespace Abraxius.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        var effectiveArguments = args.ToList();
        var allowOutOfProcess = string.Equals(
            Environment.GetEnvironmentVariable("ABRAXIUS_ALLOW_OUT_OF_PROCESS_BENCHMARKS"),
            "1",
            StringComparison.Ordinal);

        if (!allowOutOfProcess && !HasOption(effectiveArguments, "--inProcess", "-i"))
        {
            effectiveArguments.Add("--inProcess");
            Console.Error.WriteLine("Abraxius benchmark safety: using one in-process benchmark host. Set ABRAXIUS_ALLOW_OUT_OF_PROCESS_BENCHMARKS=1 only in an isolated, memory-capped environment.");
        }

        if (!HasOption(effectiveArguments, "--job", "-j"))
        {
            effectiveArguments.AddRange(["--job", "short"]);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run([.. effectiveArguments]);
    }

    private static bool HasOption(IReadOnlyList<string> arguments, string longName, string shortName) =>
        arguments.Any(argument => argument.Equals(longName, StringComparison.OrdinalIgnoreCase)
            || argument.Equals(shortName, StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith($"{longName}=", StringComparison.OrdinalIgnoreCase));
}
