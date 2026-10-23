using System.Security.Cryptography;
using System.Text;

namespace OneDriveS3Proxy.Graph;

internal static class OneDrivePathMapper
{
    internal const string DataFolder = ".onedrive-s3-proxy-objects";

    public static string BucketPath(string rootFolder, string bucket) =>
        $"{rootFolder}/{bucket}";

    public static string ObjectDataPath(string rootFolder, string bucket, string key)
    {
        var hash = Hash(key);
        return $"{BucketPath(rootFolder, bucket)}/{DataFolder}/{hash[..2]}/{hash}.data";
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
