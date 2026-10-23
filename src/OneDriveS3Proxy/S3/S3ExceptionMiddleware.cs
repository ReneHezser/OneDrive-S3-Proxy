using System.Security.Authentication;
using System.Xml.Linq;
using OneDriveS3Proxy.Graph;

namespace OneDriveS3Proxy.S3;

public sealed class S3ExceptionMiddleware(RequestDelegate next, ILogger<S3ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (S3Exception exception)
        {
            await WriteErrorAsync(context, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (AuthenticationException exception)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "SignatureDoesNotMatch", exception.Message);
        }
        catch (GraphStoreException exception)
        {
            logger.LogWarning("Graph operation failed with status {StatusCode}: {Message}", exception.StatusCode, exception.Message);
            var mapped = exception.StatusCode switch
            {
                400 => (400, "InvalidRequest"),
                404 => (404, "NoSuchKey"),
                409 => (409, "BucketNotEmpty"),
                413 => (413, "EntityTooLarge"),
                416 => (416, "InvalidRange"),
                429 => (503, "SlowDown"),
                504 => (504, "RequestTimeout"),
                _ => (502, "InternalError")
            };
            await WriteErrorAsync(context, mapped.Item1, exception.S3Code ?? mapped.Item2, exception.Message);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        var document = new XDocument(
            new XElement("Error",
                new XElement("Code", code),
                new XElement("Message", message),
                new XElement("Resource", context.Request.Path.Value ?? "/"),
                new XElement("RequestId", context.TraceIdentifier)));
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(document.ToString(SaveOptions.DisableFormatting));
    }
}
