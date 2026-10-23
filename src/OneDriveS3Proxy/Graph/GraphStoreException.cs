namespace OneDriveS3Proxy.Graph;

public sealed class GraphStoreException(int statusCode, string message, string? s3Code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? S3Code { get; } = s3Code;
}
