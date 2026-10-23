namespace OneDriveS3Proxy.S3;

public sealed class SigV4AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SigV4Verifier verifier)
    {
        if (!IsHealthProbe(context.Request))
        {
            await verifier.VerifyAsync(context.Request, context.RequestAborted);
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
