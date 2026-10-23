using System.Security.Cryptography;
using System.Text;

namespace OneDriveS3Proxy.Graph;

internal static class OneDrivePathMapper
{
    public static string BucketPath(string rootFolder, string bucket) =>
        $"{rootFolder}/{bucket}";

    public static string ObjectPath(string rootFolder, string bucket, string key)
    {
        var path = $"{BucketPath(rootFolder, bucket)}/{key}";
        if (!IsDirectKeyCompatible(key) || path.Length > 400)
        {
            throw new GraphStoreException(
                400,
                "The object key cannot be represented as a OneDrive file path. Use valid file names and a path of at most 400 characters.",
                "InvalidArgument");
        }

        return path;
    }

    public static bool IsDirectKeyCompatible(string key)
    {
        var segments = key.Split('/');
        return segments.All(segment =>
                segment.Length is > 0 and <= 255 &&
                segment is not "." and not ".." &&
                !segment.StartsWith(' ') &&
                !segment.EndsWith(' ') &&
                !segment.EndsWith('.') &&
                !segment.StartsWith("~$", StringComparison.Ordinal) &&
                !segment.Contains("_vti_", StringComparison.OrdinalIgnoreCase) &&
                !segment.Equals(".lock", StringComparison.OrdinalIgnoreCase) &&
                !segment.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) &&
                !IsReservedDeviceName(segment) &&
                segment.IndexOfAny(['"', '*', ':', '<', '>', '?', '\\', '|']) < 0 &&
                segment.All(character => !char.IsControl(character)));
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Length == 4 && char.IsAsciiDigit(name[3]) &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
