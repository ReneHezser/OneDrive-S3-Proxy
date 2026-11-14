using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OneDriveS3Proxy.Performance;

internal static class PerformanceProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(PerformanceOptions.Usage);
            return 0;
        }

        PerformanceOptions options;
        try
        {
            options = PerformanceOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(PerformanceOptions.Usage);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var results = await PerformanceRunner.RunAsync(options, Console.Out, cancellation.Token);
            using var process = Process.GetCurrentProcess();
            var report = new
            {
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Backend = "simulated-streaming",
                Transport = "HTTP/1.1 over loopback TCP",
                Scope = "Single-part PUT/GET, signed SHA-256 uploads, MD5 ETags, verified downloads; no Graph, disk, throttling or multipart",
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                LogicalProcessors = Environment.ProcessorCount,
                CombinedProcessPeakWorkingSetMiB = process.PeakWorkingSet64 / (1024d * 1024),
                Options = options,
                Results = results
            };
            var path = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true }, cancellation.Token);
            }
            Console.WriteLine($"JSON report: {path}");
            Console.WriteLine($"Combined client/server peak working set: {report.CombinedProcessPeakWorkingSetMiB:F1} MiB");
            return results.Any(result => result.Failed != 0) ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Performance test canceled; no complete report was produced.");
            return 130;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Warmup failed", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
