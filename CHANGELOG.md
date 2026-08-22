# Changelog

All notable changes to Plex Bridge are documented here.

## [1.0.3] - 2026-08-22

- Map Plex elementary video, audio, and embedded subtitle metadata into Jellyfin `MediaStream` entries.
- Add conservative fallback video/audio descriptors when Plex omits detailed stream metadata.
- Log upstream Plex HTTP status, range, content type, and content length for media proxy requests.
- Bump the Channel data version so stale media metadata is refreshed.

## [1.0.2] - 2026-08-22

- Replace Plex-style string media-source IDs with deterministic GUIDs required by Jellyfin 10.11's HLS/trickplay path.
- Bump the Channel data version to invalidate stale channel cache entries.

## [1.0.1] - 2026-08-22

- Fix the 50-item library limit by walking Plex pagination until Jellyfin's complete Channel folder has been populated.
- Treat the configured page size as an upstream Plex fetch batch size rather than a Jellyfin result cap.

## [1.0.0] - 2026-08-22

- Initial developer preview.
- Browse shared Plex movie and TV libraries through Jellyfin Channels.
- Movies, series, seasons, and episodes.
- Server-side Plex token injection and signed local stream URLs.
- Plex artwork proxying.
- HTTP byte-range forwarding for seeking.
- Optional library and Jellyfin-user allowlists.
