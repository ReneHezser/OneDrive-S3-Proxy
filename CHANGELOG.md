# Changelog

All notable changes to this project will be documented in this file.

## [0.1.8] - 2026-10-29

### Fixed

- Remove empty OneDrive parent folders after deleting the last object, including
  through recursive or multi-object S3 deletes, without removing the bucket.
- Remove empty object-metadata hash-shard and per-bucket state folders after
  deleting metadata, while preserving the state root and active multipart uploads.
  Cleanup checks for children and uses conditional deletes; writes into the
  affected payload and metadata directories must be stopped during cleanup.

## [0.1.7] - 2026-10-28

### Fixed

- Return content-MD5 ETags for single-object uploads and persist them across reads,
  listings, and copies instead of exposing OneDrive version tags to clients such
  as QNAP. Multipart completion now persists the standard MD5-of-part-MD5s ETag.

## [0.1.6] - 2026-10-28

### Added

- Automatically detected AWS Signature Version 2 authentication for legacy clients,
  including Authorization headers, presigned URLs, and streaming Content-MD5
  validation, using the existing S3 access and secret keys alongside SigV4.

### Fixed

- Reject signed payloads that exceed Content-Length or end before it, including
  additional reads after the declared length's payload hash has been validated.

## [0.1.5] - 2026-10-27

### Fixed

- Omit the redundant `item.name` property from Graph upload-session requests, which
  OneDrive for Business can reject with HTTP 400 `invalidRequest`. This fixes
  uploads larger than 4 MiB, including Barman/PostgreSQL multipart backup parts,
  while preserving existing conflict and overwrite behavior.

## [0.1.4] - 2026-10-23

### Fixed

- Resolve the folder portion of S3 listing prefixes directly and traverse only
  matching branches, avoiding scans of unrelated volume-block directories when
  Longhorn lists system backups.
- Stop searching delimiter-grouped subtrees after finding one file, while preserving
  empty-folder handling, S3 pagination, and Microsoft Graph paging.
- Skip object enumeration for listings with `max-keys=0`.

## [0.1.3] - 2026-10-23

### Changed

- Store object payloads directly under each bucket using their original S3 key paths,
  including copies and completed multipart uploads, instead of an internal DataFolder.
- Reject keys that OneDrive cannot represent safely, including unsupported names,
  case-only collisions, file/folder conflicts, and paths exceeding OneDrive's limits.
- Keep content headers, user metadata, tags, and unfinished multipart uploads in the
  separate internal state folder.

## [0.1.2] - 2026-10-23

### Added

- Configurable outbound Graph concurrency through `OneDrive__MaxConcurrency`
  (1 through 32; default `4`) and HTTP 429 retry limits through
  `OneDrive__MaxRetries` (0 through 10; default `5`).
- Shared throttling cooldown across uploads, metadata operations, and copy monitoring
  within each proxy instance.

### Fixed

- Retry Microsoft Graph throttling responses using `Retry-After` or
  `error.retryAfterSeconds`, with exponential backoff and jitter when neither is
  available, instead of immediately failing Longhorn backup block uploads.
- Safely replay upload request bodies with buffering limited to 10 MiB, while
  honoring cancellation and preserving `SlowDown` errors when retries are exhausted.

## [0.1.1] - 2026-10-23

### Added

- Persistent multipart uploads, single byte ranges, CopyObject, content headers, user
  metadata, and object tags.
- Direct Microsoft Graph storage contract tests in addition to the S3 protocol tests.
- A readiness check that validates the configured drive and root folder.
- `501 NotImplemented` responses for unsupported S3 sub-resources and UploadPartCopy.
- ListObjects v1 `marker`/`NextMarker` and ListObjectsV2 `start-after` support.

### Changed

- Publish multi-architecture (`linux/amd64` and `linux/arm64`) container images; the
  build stage runs natively, so no emulation is required.
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
- GitHub Actions CI for PR testing, Docker builds, and `main`-branch GHCR publishing
  with changelog-derived version tags, plus weekly Dependabot updates.

### Known limitations

- Uploads require `Content-Length` and are limited to 250 MiB per object.
- Multipart uploads, range reads, ACLs, versioning, object metadata, server-side
  encryption, bucket policies, and virtual-hosted bucket URLs are not supported.
- Object listing traverses the OneDrive bucket tree and may be constrained by
  Microsoft Graph throttling and listing time for large buckets.
