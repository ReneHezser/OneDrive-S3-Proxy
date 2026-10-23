using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace OneDriveS3Proxy.Graph;

public interface IGraphTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class GraphTokenProvider : IGraphTokenProvider
{
    private static readonly TokenRequestContext TokenContext = new(["https://graph.microsoft.com/.default"]);
    private readonly ClientSecretCredential _credential;

    public GraphTokenProvider(IOptions<ProxyOptions> options)
    {
        var entra = options.Value.Entra;
        _credential = new ClientSecretCredential(entra.TenantId, entra.ClientId, entra.ClientSecret);
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(TokenContext, cancellationToken);
        return token.Token;
    }
}
