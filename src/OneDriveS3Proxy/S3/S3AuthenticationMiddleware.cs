using System.Security.Authentication;

namespace OneDriveS3Proxy.S3;

public sealed class S3AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SigV4Verifier sigV4Verifier, SigV2Verifier sigV2Verifier)
    {
        if (!IsHealthProbe(context.Request))
        {
            var request = context.Request;
            var sigV2 = request.Headers.Authorization.ToString().StartsWith("AWS ", StringComparison.Ordinal) ||
                request.Query.ContainsKey("AWSAccessKeyId") || request.Query.ContainsKey("Signature");
            if (sigV2)
            {
                if (request.Query.ContainsKey("X-Amz-Signature"))
                {
                    throw new AuthenticationException("Signature Version 2 and Version 4 authentication cannot be combined.");
                }

                await sigV2Verifier.VerifyAsync(request, context.RequestAborted);
            }
            else
            {
                await sigV4Verifier.VerifyAsync(request, context.RequestAborted);
            }
        }

        await next(context);
    }

    // Only the exact probe endpoints are anonymous; every other path, including keys in a
    // bucket named "health", must be signed.
    private static bool IsHealthProbe(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        (request.Path.Equals("/health/live", StringComparison.Ordinal) ||
         request.Path.Equals("/health/ready", StringComparison.Ordinal));
}
