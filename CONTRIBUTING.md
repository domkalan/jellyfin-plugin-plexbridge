# Contributing

Contributions are welcome. Plex Bridge is intentionally small enough that changes should be easy to review and test.

## Development requirements

- .NET 9 SDK, or Docker/Podman for the containerized build
- Jellyfin 10.11.x for integration testing
- Access to a Plex Media Server that your Plex account is authorized to use

## Build

```bash
./scripts/package.sh
```

Or without a local .NET SDK:

```bash
./scripts/package-docker.sh
```

The default target ABI is Jellyfin 10.11.11. To test another 10.11.x patch:

```bash
JELLYFIN_VERSION=10.11.10 ./scripts/package.sh
```

Jellyfin plugin package references should match the server version being tested.

## Before opening a pull request

```bash
./scripts/validate-source.sh
```

If you have the .NET SDK installed, also run:

```bash
dotnet build PlexBridge.slnx -c Release
```

For playback changes, test at minimum:

1. Movies and TV libraries browse beyond 50 items.
2. A simple H.264/AAC or H.264/AC3 item starts in Jellyfin Web.
3. Seeking produces successful HTTP range requests.
4. Plex credentials never appear in Jellyfin client-facing URLs or logs.

## Versioning

`VERSION`, `build.yaml`, and the default project version should be bumped together for a release. The release workflow uses the Git tag as the authoritative package version.

Release tags use the form `v1.0.3`. Pushing a tag builds the release ZIP, creates a GitHub Release, computes Jellyfin's MD5 package checksum, and updates `manifest.json` on the default branch.
