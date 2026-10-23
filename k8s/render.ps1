[CmdletBinding()]
param(
    [string] $ConfigMapPath = (Join-Path $PSScriptRoot 'configmap.yaml')
)

$ErrorActionPreference = 'Stop'

$configMap = Get-Content -LiteralPath $ConfigMapPath -Raw
$urlEntries = [regex]::Matches($configMap, '(?m)^  S3__URL: "([^"\r\n]+)"\s*$')
if ($urlEntries.Count -ne 1) {
    throw 'The ConfigMap must contain exactly one S3__URL entry with a double-quoted value.'
}

$url = $urlEntries[0].Groups[1].Value
$listener = [regex]::Match($url, '^http://(?:\+|\*|\[[0-9a-fA-F:]+\]|[a-zA-Z0-9.-]+):([0-9]{1,5})/?$')
if (-not $listener.Success) {
    throw 'S3__URL must be a single HTTP listener URL with an explicit port, such as http://+:9000. Terminate TLS at the ingress.'
}

$port = [int] $listener.Groups[1].Value
if ($port -lt 1 -or $port -gt 65535) {
    throw 'The S3__URL port must be between 1 and 65535.'
}

$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'deployment.template.yaml') -Raw
if (-not $template.Contains('__S3_PORT__')) {
    throw 'The deployment template is missing its __S3_PORT__ placeholder.'
}

$configMap.TrimEnd()
'---'
$template.Replace('__S3_PORT__', [string] $port).TrimEnd()
