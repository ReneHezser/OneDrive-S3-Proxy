using OneDriveS3Proxy;
using OneDriveS3Proxy.Graph;
using OneDriveS3Proxy.S3;

var builder = WebApplication.CreateBuilder(args);

// Kestrel's 30 MB default would reject uploads far below the configured object limit.
// The extra MiB leaves room for protocol overhead.
builder.WebHost.ConfigureKestrel((context, options) =>
{
    var maxUploadMiB = Math.Clamp(context.Configuration.GetValue("S3:MaxUploadSizeMiB", 238000), 1, 238000);
    options.Limits.MaxRequestBodySize = (maxUploadMiB + 1L) * 1024 * 1024;
});

builder.Services
    .AddOptions<ProxyOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services
    .AddHttpClient("MicrosoftGraph", client =>
    {
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        client.Timeout = TimeSpan.FromMinutes(10);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    });
builder.Services
    .AddHttpClient("GraphMonitor")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    });
builder.Services.AddSingleton<IGraphTokenProvider, GraphTokenProvider>();
builder.Services.AddSingleton<IOneDriveStore>(services =>
{
    var clientFactory = services.GetRequiredService<IHttpClientFactory>();
    return new OneDriveStore(
        clientFactory.CreateClient("MicrosoftGraph"),
        clientFactory,
        services.GetRequiredService<IGraphTokenProvider>(),
        services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>());
});
builder.Services.AddSingleton<SigV4Verifier>();

var app = builder.Build();

app.UseMiddleware<S3ExceptionMiddleware>();
app.UseMiddleware<SigV4AuthenticationMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IOneDriveStore store, HttpContext context) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));
    await store.CheckReadyAsync(timeout.Token);
    return Results.Ok(new { status = "ready" });
});

S3Endpoints.Map(app);

app.Run();

public partial class Program;
