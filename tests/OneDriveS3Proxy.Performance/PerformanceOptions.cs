using System.Globalization;

namespace OneDriveS3Proxy.Performance;

internal sealed record PerformanceOptions(int[] SizesMiB, int[] Concurrency, int Objects, int TimeoutSeconds, string Output)
{
    public const string Usage = """
        Local proxy performance test (simulated OneDrive; no cloud requests).
        dotnet run -c Release --project tests\OneDriveS3Proxy.Performance -- [options]
          --sizes-mib 1,16,1024       Object sizes in MiB (1..238000)
          --concurrency 1,2,4,8,16    Concurrent file requests
          --objects 32               Objects per size/concurrency combination
          --timeout-seconds 600      Deadline for each complete request, including its body
          --output <path>            JSON report (default: artifacts\performance\<timestamp>.json)
          --help                     Show help
        """;

    public static PerformanceOptions Parse(string[] args)
    {
        var sizes = new[] { 1, 16, 1024 };
        var concurrency = new[] { 1, 2, 4, 8, 16 };
        var objects = 32;
        var timeout = 600;
        var output = Path.Combine("artifacts", "performance", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length)
            {
                throw new ArgumentException($"Missing value for {args[index]}.");
            }
            var value = args[index + 1];
            switch (args[index])
            {
                case "--sizes-mib":
                    sizes = PositiveList(value);
                    if (sizes.Any(size => size > 238000))
                    {
                        throw new ArgumentException("Object sizes must not exceed 238000 MiB.");
                    }
                    break;
                case "--concurrency": concurrency = PositiveList(value); break;
                case "--objects": objects = Positive(value); break;
                case "--timeout-seconds": timeout = Positive(value); break;
                case "--output":
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    output = value;
                    break;
                default: throw new ArgumentException($"Unknown option: {args[index]}.");
            }
        }

        return new PerformanceOptions(sizes, concurrency, objects, timeout, output);
    }

    private static int[] PositiveList(string value) => value.Split(',').Select(Positive).Distinct().ToArray();

    private static int Positive(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException($"Expected a positive integer, got '{value}'.");
}
