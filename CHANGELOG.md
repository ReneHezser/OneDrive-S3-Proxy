# Changelog

All notable changes to this project will be documented in this file.

## [0.1.1] - 2026-10-23

### Added

- Persistent multipart uploads, single byte ranges, CopyObject, content headers, user
  metadata, and object tags.
- Direct Microsoft Graph storage contract tests in addition to the S3 protocol tests.
- A readiness check that validates the configured drive and root folder.
- `501 NotImplemented` responses for unsupported S3 sub-resources and UploadPartCopy.
- ListObjects v1 `marker`/`NextMarker` and ListObjectsV2 `start-after` support.

### Changed

- Store object payloads at hash-addressed paths so case-sensitive S3 keys and keys with
  repeated slashes or OneDrive-reserved characters remain distinct.
- Raise the configurable object-size limit to 238,000 MiB, below OneDrive's 250 GB
  per-file limit.
- Require `host` and all `x-amz-*` request headers to be covered by the SigV4 signature.
- List objects with bounded parallelism, one directory listing per shard, and an
  in-memory key cache instead of two sequential Graph calls per object.
- Pin the Kubernetes deployment image to a version tag instead of `latest`.

### Fixed

- Only the exact `GET /health/live` and `GET /health/ready` probes skip authentication;
  previously every path under `/health`, including a bucket named `health`, did.
- Validate the request timestamp of header-signed requests (it was never checked) and
  accept presigned URLs from clients whose clocks are slightly ahead.
- Raise Kestrel's 30 MB request-body limit to the configured object-size limit.
- Fail startup when nested Entra, OneDrive, or S3 settings (including the access and
  secret keys) are missing, instead of accepting empty credentials.
- Stop `?acl`, `?versionId`, `?policy` and similar requests from overwriting or
  deleting data as plain object or bucket operations.
- Make ListObjects with `max-keys=0` report `IsTruncated=false` and stop v1 clients
  from looping on the first page.
- Retry transient failures when persisting object state so uploaded objects are not
  left missing from listings.
- Preserve an existing destination when an asynchronous Graph copy fails.
- Prevent concurrent multipart part uploads from pairing a body with the wrong ETag.
- Prevent multipart completion from recreating a deleted bucket.
- Return `NoSuchUpload` and `NoSuchBucket` for the corresponding S3 error conditions.

## [0.1.0] - 2026-10-22

Initial release.

### Added

- ASP.NET Core proxy exposing a focused, path-style S3-compatible API backed by
  OneDrive for Business through Microsoft Graph.
- AWS Signature Version 4 authentication for request headers and presigned URLs.
- Bucket listing, creation, existence checks, location lookup, and deletion.
- ListObjectsV2 with prefix, delimiter, maximum-key, and continuation-token support.
- Single-part object uploads, downloads, existence checks, and deletion, including
  multi-object deletion.
- Microsoft Entra client-credentials authentication with cached Graph access tokens.
- Environment-based configuration, a Dockerfile, and Kubernetes deployment manifests.
- Longhorn backup target and s3manager configuration examples and compatibility tests.
- GitHub Actions CI for PR testing, Docker builds, and default-branch GHCR publishing
  with changelog-derived version tags, plus weekly Dependabot updates.

### Known limitations

- Uploads require `Content-Length` and are limited to 250 MiB per object.
- Multipart uploads, range reads, ACLs, versioning, object metadata, server-side
  encryption, bucket policies, and virtual-hosted bucket URLs are not supported.
- Object listing traverses the OneDrive bucket tree and may be constrained by
  Microsoft Graph throttling and listing time for large buckets.
