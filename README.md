# Plex Bridge for Jellyfin

Plex Bridge exposes remote Plex **movie and TV libraries** as a Jellyfin Channel while keeping Plex credentials on the Jellyfin server.

It is designed for a Jellyfin household that has authorized access to another Plex Media Server and wants to browse and play that shared media from Jellyfin instead of switching clients.

> **Status:** early public release. Plex Bridge 1.0.3 has working remote library browsing and playback on Jellyfin 10.11.11, including large libraries, Jellyfin HLS playback, stream metadata, and HTTP range forwarding. See [Known limitations](#known-limitations) before publishing it as production-ready software.

## Features

- Remote Plex movie libraries
- Remote Plex TV libraries
- Series -> Seasons -> Episodes hierarchy
- Complete library pagination rather than a 50-item cap
- Plex metadata, ratings, genres, years, studios, and common provider IDs
- Proxied Plex artwork
- Multiple Plex media versions exposed to Jellyfin
- Plex video/audio/embedded-subtitle stream metadata mapped to Jellyfin `MediaStream` entries
- Server-side Plex token injection
- Signed local playback URLs that do not expose the Plex token
- HTTP `Range` forwarding and `206 Partial Content` relay for seeking
- Jellyfin-side remuxing/transcoding
- Optional Plex-library allowlist
- Optional Jellyfin-user allowlist

## Security model

Plex Bridge never puts `X-Plex-Token` in a Jellyfin client-facing media URL or `RequiredHttpHeaders` value.

```text
Jellyfin client
    -> Jellyfin playback/transcode endpoint
        -> signed /PlexBridge/stream/... URL
            -> Plex Bridge validates signature + expiry
                -> Plex request with X-Plex-Token (server-side only)
                    -> media bytes/ranges relayed back through Jellyfin
```

Signed URLs authorize one Plex rating-key/media/part tuple until their expiry. They are bearer capabilities, so they should still be treated as temporary secrets.

## Compatibility

The current release target is:

- Jellyfin Server **10.11.11**
- .NET **9**

Jellyfin plugin packages are ABI-sensitive. The `Jellyfin.Controller` and `Jellyfin.Model` package versions should match the Jellyfin server version being targeted.

## Install from the Jellyfin plugin catalog

Once this repository has at least one published GitHub release, add its manifest URL in Jellyfin:

1. Open **Dashboard -> Plugins -> Repositories**.
2. Add a repository named **Plex Bridge**.
3. Use:

```text
https://raw.githubusercontent.com/domkalan/jellyfin-plugin-plexbridge/main/manifest.json
```

4. Save.
5. Open **Catalog**, find **Plex Bridge**, and install it.
6. Restart Jellyfin.
7. Open **Dashboard -> Plugins -> Plex Bridge** and configure it.

The repository's release workflow automatically writes the real GitHub release URL, MD5 checksum, target ABI, version, and timestamp into `manifest.json`.

## Manual build

### .NET SDK

Requirements:

- .NET 9 SDK
- `zip`

```bash
./scripts/package.sh
```

The version defaults to the value in `VERSION`.

To build a different plugin version:

```bash
./scripts/package.sh 1.0.4
```

To target another Jellyfin 10.11.x patch:

```bash
JELLYFIN_VERSION=10.11.10 ./scripts/package.sh 1.0.3
```

Output:

```text
dist/PlexBridge-1.0.3-jellyfin-10.11.11.zip
```

The ZIP contains the plugin files at its root because Jellyfin's repository installer creates the versioned plugin directory itself.

### Docker-only build

```bash
./scripts/package-docker.sh
```

Output is written to `docker-dist/`.

## Manual installation

For a manually downloaded release ZIP, create a plugin folder yourself and extract the ZIP into it.

Example for the official Jellyfin Docker image where `/config` is your persistent configuration volume:

```bash
docker stop jellyfin
mkdir -p '/path/to/jellyfin/config/plugins/Plex Bridge_1.0.3.0'
unzip PlexBridge-1.0.3-jellyfin-10.11.11.zip \
  -d '/path/to/jellyfin/config/plugins/Plex Bridge_1.0.3.0'
docker start jellyfin
```

The resulting layout should be:

```text
/config/plugins/
└── Plex Bridge_1.0.3.0/
    ├── Jellyfin.Plugin.PlexBridge.dll
    ├── Jellyfin.Plugin.PlexBridge.pdb   # when packaged
    └── LICENSE
```

## Configuration

### Plex server URL

Use the reachable URL for the Plex Media Server that owns the shared libraries, not `plex.tv`.

Example:

```text
https://example.plex.direct:32400
```

### Plex access token

Use a Plex access token belonging to an account that is authorized to access the remote server and libraries.

The token is persisted in Jellyfin's plugin configuration and used server-side only. Do not post it in GitHub issues or logs.

### Internal Jellyfin URL

This is the URL Jellyfin's own FFmpeg process uses to reach the Plex Bridge streaming proxy.

Typical value:

```text
http://127.0.0.1:8096
```

If Jellyfin uses a Base URL such as `/jellyfin`:

```text
http://127.0.0.1:8096/jellyfin
```

### Plex library section keys

Leave blank to expose all shared movie and TV sections. To restrict the plugin, enter comma-separated Plex section keys.

### Allowed Jellyfin user IDs

Leave blank to allow all Jellyfin users to browse the Channel, or enter comma-separated Jellyfin user GUIDs.

### Plex fetch batch size

Plex Bridge fetches large folders from Plex in batches and assembles the complete Channel folder for Jellyfin. The default is 50 records per Plex request; it is **not** a 50-item library limit.

## Architecture

```text
PlexChannel (IChannel + IRequiresMediaInfoCallback)
        |
        +-- PlexClient ----------------------------> Plex Media Server
        |      | library XML / metadata                  ^
        |      +-----------------------------------------+
        |
        +-- ProxyTokenService
        |      | signed local URL
        |      v
        +--> PlexBridgeController
                   | validates HMAC + expiry
                   | resolves Plex media part
                   | injects X-Plex-Token
                   | forwards Range / Content-Range
                   v
              Plex Media Server
```

## Known limitations

The current direct-source implementation does **not** yet provide:

- Plex PIN/JWT sign-in and automatic shared-server discovery
- Plex universal playback-decision reservations
- Plex-managed transcoding fallback when the remote server refuses original-quality media because of bandwidth policy
- External Plex subtitle sidecars as signed Jellyfin subtitle streams
- Watch-state/timeline synchronization back to Plex
- Plex collections, Watchlist, Live TV, music, or photos
- Correct concatenation of legacy multi-part movie files; only the first part is exposed
- A native Jellyfin library/STRM mode for clients with weak Channel support

Channel support varies between Jellyfin clients. Jellyfin Web is the recommended first compatibility target.

## Troubleshooting

### Channel is empty after correcting credentials

Jellyfin caches Channel folders. Current releases increment their Channel data version when cache-breaking metadata behavior changes, but an older failed result may still require a server restart or Channel cache refresh during development.

## License

Plex Bridge is licensed under the **GNU General Public License v3.0**. See [`LICENSE`](LICENSE).

Plex and Jellyfin are separate projects. Plex Bridge is not affiliated with or endorsed by Plex, Inc. or the Jellyfin project.
