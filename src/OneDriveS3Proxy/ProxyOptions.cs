using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace OneDriveS3Proxy;

public sealed class ProxyOptions
{
    [Required]
    [ValidateObjectMembers]
    public EntraOptions Entra { get; init; } = new();

    [Required]
    [ValidateObjectMembers]
    public OneDriveOptions OneDrive { get; init; } = new();

    [Required]
    [ValidateObjectMembers]
    public S3Options S3 { get; init; } = new();
}

public sealed class EntraOptions
{
    [Required]
    public string TenantId { get; init; } = string.Empty;

    [Required]
    public string ClientId { get; init; } = string.Empty;

    [Required]
    public string ClientSecret { get; init; } = string.Empty;
}

public sealed class OneDriveOptions
{
    [Required]
    public string UserId { get; init; } = string.Empty;

    [Required]
    public string RootFolder { get; init; } = "S3";
}

public sealed class S3Options
{
    [Required]
    public string AccessKey { get; init; } = string.Empty;

    [Required]
    public string SecretKey { get; init; } = string.Empty;

    [Required]
    public string Region { get; init; } = "us-east-1";

    [Range(1, 238000)]
    public int MaxUploadSizeMiB { get; init; } = 238000;
}
