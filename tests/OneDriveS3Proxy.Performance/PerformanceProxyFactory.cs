using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.Performance;

internal sealed class PerformanceProxyFactory(IReadOnlyDictionary<long, PayloadDigest> digests)
    : WebApplicationFactory<global::Program>
{
    public const string AccessKey = "local-performance-access";
    public const string SecretKey = "local-performance-secret-not-for-deployment";
    public const string Region = "us-east-1";
    public ConcurrentDictionary<string, ConcurrencyCounter> ServerCounters { get; } = new();
    public SimulatedOneDriveStore Store { get; } = new(digests);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Entra:TenantId"] = "simulated",
                ["Entra:ClientId"] = "simulated",
                ["Entra:ClientSecret"] = "simulated",
                ["OneDrive:UserId"] = "simulated",
                ["S3:URL"] = "http://127.0.0.1:0",
                ["S3:AccessKey"] = AccessKey,
                ["S3:SecretKey"] = SecretKey,
                ["S3:Region"] = Region,
                ["S3:MaxUploadSizeMiB"] = "238000"
            }));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOneDriveStore>();
            services.AddSingleton<IOneDriveStore>(Store);
            services.AddSingleton<IStartupFilter>(new TrackingFilter(ServerCounters));
        });
    }

    public static string CounterKey(string method, string path) => $"{method}:{path[..path.LastIndexOf('/')]}";

    private sealed class TrackingFilter(ConcurrentDictionary<string, ConcurrencyCounter> counters) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                var path = context.Request.Path.Value!;
                if (!path.StartsWith($"/{SimulatedOneDriveStore.Bucket}/", StringComparison.Ordinal))
                {
                    await nextRequest(context);
                    return;
                }

                var counter = counters.GetOrAdd(CounterKey(context.Request.Method, path), _ => new());
                counter.Enter();
                try
                {
                    await nextRequest(context);
                }
                finally
                {
                    counter.Exit();
                }
            });
            next(app);
        };
    }
}

internal sealed class ConcurrencyCounter
{
    private int _active;
    private int _peak;
    public int Peak => Volatile.Read(ref _peak);

    public void Enter()
    {
        var active = Interlocked.Increment(ref _active);
        var previous = Peak;
        while (active > previous)
        {
            var observed = Interlocked.CompareExchange(ref _peak, active, previous);
            if (observed == previous)
            {
                break;
            }
            previous = observed;
        }
    }

    public void Exit() => Interlocked.Decrement(ref _active);
}
