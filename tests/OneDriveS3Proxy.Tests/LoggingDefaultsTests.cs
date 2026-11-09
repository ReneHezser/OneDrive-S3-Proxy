using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OneDriveS3Proxy.Tests;

public sealed class LoggingDefaultsTests(LonghornProxyFactory factory) : IClassFixture<LonghornProxyFactory>
{
    [Theory]
    [InlineData("System.Net.Http.HttpClient.MicrosoftGraph.ClientHandler")]
    [InlineData("System.Net.Http.HttpClient.MicrosoftGraph.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.GraphMonitor.ClientHandler")]
    public void GraphHttpClientRequestLogs_AreQuietByDefault(string category)
    {
        var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        var logger = loggerFactory.CreateLogger(category);

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Theory]
    [InlineData("OneDriveS3Proxy.Graph.GraphThrottlingHandler")]
    [InlineData("OneDriveS3Proxy.Graph.OneDriveStore")]
    public void ProxyLogs_RemainAtInformation(string category)
    {
        var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        Assert.True(loggerFactory.CreateLogger(category).IsEnabled(LogLevel.Information));
    }
}
