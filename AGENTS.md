# AGENTS.md

## Project Overview

Plex Bridge is a Jellyfin plugin that exposes remote Plex movie and TV libraries
as a Jellyfin Channel.

The plugin:

- Connects to a Plex Media Server using a user-provided Plex access token.
- Browses Plex movie and TV libraries.
- Maps Plex metadata into Jellyfin Channel items.
- Streams Plex media through an authenticated Jellyfin-side proxy.
- Keeps Plex credentials server-side.
- Supports HTTP range requests for seeking.
- Provides Jellyfin with Plex media stream metadata for playback/transcoding.

The current stable development target is Jellyfin 10.11.x.

---

## Repository Structure

Primary source code lives in:

```text
Jellyfin.Plugin.PlexBridge/
├── Channels/
│   └── PlexChannel.cs
├── Configuration/
├── Controllers/
├── Services/
│   ├── PlexClient.cs
│   ├── ProxyTokenService.cs
│   └── ...
├── Plugin.cs
└── Jellyfin.Plugin.PlexBridge.csproj
```

Supporting files include:

```text
scripts/
.github/workflows/
manifest.json
build.yaml
README.md
CHANGELOG.md
PUBLISHING.md
```

Keep Plex-specific API logic in services where possible rather than embedding
HTTP behavior directly into Jellyfin Channel implementations.

---

## Build

The preferred reproducible build method is Docker.

```bash
sudo bash scripts/package-docker.sh <version>
```

Example:

```bash
sudo bash scripts/package-docker.sh 1.0.3
```

The build must produce a Jellyfin repository-compatible ZIP such as:

```text
docker-dist/PlexBridge-1.0.3-jellyfin-10.11.11.zip
```

The plugin DLL must be at the root of the release ZIP.

Do NOT add an extra directory such as:

```text
Plex Bridge_1.0.3.0/
```

inside repository release ZIPs. Jellyfin creates the plugin version directory
when installing from a repository.

---

## Jellyfin Compatibility

Jellyfin plugins are ABI-sensitive.

The package versions for Jellyfin libraries in the `.csproj` must match the
target Jellyfin server version.

Do not casually upgrade:

- Jellyfin.Controller
- Jellyfin.Model
- Jellyfin.Common
- target framework

without verifying compatibility with the target Jellyfin release.

When compatibility changes, also update:

- `build.yaml`
- release scripts
- GitHub Actions
- `manifest.json` generation
- README compatibility documentation

The current branch primarily targets Jellyfin 10.11.x.

---

## Channel Behavior

Plex Bridge uses Jellyfin's Channel API.

Important behavior:

### Do not assume Jellyfin supplies pagination

Jellyfin may call `GetChannelItems()` without a `StartIndex` or `Limit`.

When the request is unbounded, Plex Bridge must fetch all Plex pages internally.

Do NOT change the Plex fetch batch size into a global result limit.

Correct behavior:

```text
Jellyfin requests folder
        ↓
PlexBridge fetches:
0-49
50-99
100-149
...
        ↓
returns complete folder
```

### Channel cache

Jellyfin caches Channel results aggressively.

When a change invalidates previously cached Channel data, increment the
Channel `DataVersion`.

Examples:

- changing item IDs
- changing media source IDs
- changing hierarchy
- changing playback metadata
- changing serialized Channel item behavior

Avoid requiring users to manually delete Jellyfin's Channel cache.

---

## Plex Authentication and Security

Plex access tokens are secrets.

Never:

- log Plex access tokens
- include Plex tokens in Jellyfin client-visible URLs
- include Plex tokens in `MediaSourceInfo.RequiredHttpHeaders`
- include Plex tokens in generated STRM files
- expose Plex tokens through API responses
- commit real Plex credentials to the repository

All authenticated Plex requests should originate server-side.

Playback URLs provided to Jellyfin must use the Plex Bridge proxy.

Example:

```text
http://127.0.0.1:8096/PlexBridge/stream/34566/0/0?exp=...&sig=...
```

The proxy should validate a short-lived signature and then attach the Plex
credential to the upstream Plex request internally.

Treat any code that could expose `X-Plex-Token` to a Jellyfin client as a
security regression.

---

## Stream Proxy Requirements

The Plex Bridge proxy must behave like a streaming proxy, not a downloader.

It must:

- stream response bodies without buffering the entire media file
- forward HTTP `Range` requests
- preserve Plex `206 Partial Content` responses
- preserve `Content-Range`
- preserve `Accept-Ranges`
- preserve appropriate `Content-Length`
- preserve media content types
- propagate cancellation when playback stops

Seeking is a core feature and must not be broken.

Do not read complete media files into memory.

---

## MediaSource IDs

Jellyfin's playback/HLS pipeline expects media source identifiers to be valid
GUIDs in some code paths.

Do NOT use IDs such as:

```text
plex:34566:0:0
```

for `MediaSourceInfo.Id`.

Plex Bridge uses stable deterministic GUIDs derived from the Plex item and
media version.

MediaSource IDs must be:

- valid GUIDs
- deterministic
- stable across refreshes
- unique for different Plex media versions/parts

Changing this behavior can break Jellyfin HLS/trickplay playback.

---

## Media Stream Metadata

Do not return an empty `MediaStreams` collection for playable video unless
there is a very good reason.

Parse Plex `<Stream>` information and map it into Jellyfin `MediaStream`
objects.

Important stream types:

- video
- audio
- subtitles

Useful properties include:

- stream index
- codec
- language
- width
- height
- bitrate
- audio channels
- sample rate
- default flag
- forced flag

If Plex does not provide usable stream metadata, preserve the existing
fallback behavior that lets Jellyfin/FFmpeg discover video and audio streams.

Changes here must be tested against Jellyfin's HLS/transcoding pipeline.

---

## Plex API Behavior

Keep Plex HTTP operations centralized in `PlexClient` or related service
classes.

Prefer strongly typed helpers over constructing Plex URLs throughout the
plugin.

Plex pagination uses:

```text
X-Plex-Container-Start
X-Plex-Container-Size
```

Plex failures should:

- log useful HTTP status information
- never log credentials
- surface enough context for debugging

Typical important responses include:

- `200` / `206`: successful media access
- `401`: invalid or inappropriate Plex token
- `403`: access denied
- `503`: Plex may require another playback path
- `509`: Plex bandwidth/direct-play limitation

---

## Playback Architecture

Preferred playback path:

```text
Jellyfin client
      ↓
Jellyfin playback pipeline
      ↓
PlexBridge signed proxy URL
      ↓
PlexBridge adds Plex authentication
      ↓
Plex Media Server
```

Do not bypass the proxy by handing authenticated Plex URLs directly to
Jellyfin clients.

The current implementation primarily targets direct access to the Plex media
part and allows Jellyfin to perform remuxing/transcoding.

Future Plex-native transcoding/playback-decision support should be implemented
without breaking the direct-play path.

---

## Artwork

Plex artwork may also require authentication.

Do not expose tokenized Plex artwork URLs directly to clients.

Use the Plex Bridge image proxy/cache when authenticated artwork is required.

---

## Subtitles

Embedded subtitle streams may be mapped through Plex media metadata.

External Plex subtitle files require authenticated retrieval and therefore
must use a Plex Bridge proxy endpoint.

Do not expose external subtitle URLs containing Plex tokens.

---

## Logging

Use structured, useful logging.

Good:

```text
Plex Bridge stream 34566/0/0 returned Plex HTTP 206
```

Good:

```text
Plex request /library/sections failed with status 401
```

Bad:

```text
Request failed:
https://plex.example.com/library/sections?X-Plex-Token=SECRET
```

Never log secrets.

Avoid noisy per-byte or per-chunk logs.

Debug logging should make it possible to determine whether a failure occurred
in:

1. Jellyfin
2. Plex Bridge
3. the Plex HTTP API
4. the Plex media endpoint
5. FFmpeg

---

## Configuration Changes

Configuration changes involving Plex connectivity, library selection, or
Channel-visible data may require Channel cache invalidation.

When modifying configuration fields:

- provide safe defaults
- preserve backward compatibility when practical
- avoid silently resetting existing values
- never serialize secrets into client-visible objects

---

## Coding Style

Follow existing project conventions.

General rules:

- Enable nullable reference type correctness.
- Prefer `async`/`await` for network and stream operations.
- Pass `CancellationToken` through asynchronous calls.
- Dispose `HttpRequestMessage`, responses, streams, and cryptographic objects
  appropriately.
- Prefer dependency injection over static service access.
- Avoid blocking calls such as `.Result` and `.Wait()`.
- Keep controller methods thin.
- Keep Plex parsing/API behavior in service classes.
- Avoid unnecessary abstractions when a small service or helper is sufficient.

Run formatting before committing when tooling is available.

---

## Testing Changes

At minimum, changes affecting playback should be smoke-tested with:

1. Jellyfin Web
2. a simple H.264/AAC or H.264/AC3 file
3. playback from the beginning
4. seeking to the middle of the file
5. stopping/resuming playback
6. a TV episode
7. a movie
8. a library containing more than 50 items

Watch the Jellyfin log during playback.

Verify that FFmpeg receives a Plex Bridge URL, not a tokenized Plex URL.

Example expected input:

```text
-i "http://127.0.0.1:8096/PlexBridge/stream/..."
```

Verify Plex Bridge reports an upstream success response such as:

```text
Plex HTTP 200
```

or:

```text
Plex HTTP 206
```

---

## Regression Checklist

Before merging changes to browsing or playback, verify:

- [ ] Plugin loads in Jellyfin.
- [ ] Plex authentication works.
- [ ] Movie libraries appear.
- [ ] TV libraries appear.
- [ ] Series → season → episode hierarchy works.
- [ ] Libraries larger than 50 items fully populate.
- [ ] Artwork loads.
- [ ] Playback starts.
- [ ] MediaSource IDs are GUIDs.
- [ ] Jellyfin receives video/audio stream metadata.
- [ ] FFmpeg reads from the Plex Bridge proxy.
- [ ] Seeking works.
- [ ] Plex credentials do not appear in client-visible URLs.
- [ ] Plex credentials do not appear in logs.
- [ ] Release ZIP has the correct root layout.

---

## Versioning and Releases

Use semantic versions for releases:

```text
1.0.3
1.0.4
1.1.0
```

Jellyfin manifests use four-part versions:

```text
1.0.3.0
```

Release tags should use:

```text
v1.0.3
```

Do not manually invent checksums in `manifest.json`.

The release workflow should:

1. build the plugin
2. package the repository-compatible ZIP
3. create the GitHub Release
4. calculate the checksum of that exact ZIP
5. update `manifest.json`
6. preserve previous manifest versions
7. keep newest versions first

See `PUBLISHING.md` for the complete release process.

---

## manifest.json

`manifest.json` is a Jellyfin plugin repository manifest, not the plugin's
runtime configuration.

Release entries must contain valid values for:

- `version`
- `targetAbi`
- `sourceUrl`
- `checksum`
- `timestamp`

`sourceUrl` must point to the exact published GitHub Release ZIP.

Do not commit placeholder release URLs.

An empty manifest is acceptable before the first release:

```json
[]
```

---

## Scope Guidance

Good contributions include:

- Plex authentication improvements
- automatic Plex server discovery
- additional metadata mapping
- playback reliability
- Plex playback-decision support
- Plex transcoding fallback
- subtitle support
- artwork caching
- Jellyfin client compatibility
- tests
- logging and diagnostics
- configuration UX improvements

Avoid unrelated large refactors while fixing isolated bugs.

For playback bugs, prefer identifying the failing boundary first:

```text
Jellyfin → PlexBridge → Plex → PlexBridge → FFmpeg → Jellyfin client
```

Fix the smallest responsible layer.

---

## Current Known Limitations

Before changing architecture, check `README.md`, `CHANGELOG.md`, and open
issues.

Current development areas may include:

- Plex-native transcoding fallback
- external subtitle proxying
- automatic Plex sign-in/server discovery
- expanded client compatibility testing
- watched-state synchronization
- richer Plex metadata
- Jellyfin 12 compatibility

Do not describe an unimplemented feature as supported without verifying it.

---

## Security Rule

If a proposed change makes implementation easier by exposing the Plex token to
a Jellyfin client, do not implement it.

Credential isolation takes priority over convenience.
