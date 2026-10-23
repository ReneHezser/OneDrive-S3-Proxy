namespace OneDriveS3Proxy.S3;

public sealed class S3Exception(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
