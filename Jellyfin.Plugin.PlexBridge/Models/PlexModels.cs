using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PlexBridge.Models;

/// <summary>
/// A Plex library section.
/// </summary>
public sealed class PlexLibrary
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Thumb { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>
/// A paged Plex response.
/// </summary>
public sealed class PlexPage
{
    public IReadOnlyList<PlexItem> Items { get; init; } = Array.Empty<PlexItem>();
    public int TotalCount { get; init; }
}

/// <summary>
/// Plex metadata item used by the channel mapper.
/// </summary>
public sealed class PlexItem
{
    public string RatingKey { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? ContentRating { get; init; }
    public string? Studio { get; init; }
    public string? Thumb { get; init; }
    public string? Art { get; init; }
    public string? ParentTitle { get; init; }
    public string? GrandparentTitle { get; init; }
    public int? Year { get; init; }
    public int? Index { get; init; }
    public int? ParentIndex { get; init; }
    public long? DurationMs { get; init; }
    public float? Rating { get; init; }
    public DateTime? OriginallyAvailableAt { get; init; }
    public DateTime? AddedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<PlexMedia> Media { get; init; } = Array.Empty<PlexMedia>();
}

/// <summary>
/// Plex media version.
/// </summary>
public sealed class PlexMedia
{
    public int Index { get; init; }
    public string? Container { get; init; }
    public string? VideoResolution { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int? BitrateKbps { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? AudioChannels { get; init; }
    public long? DurationMs { get; init; }
    public IReadOnlyList<PlexPart> Parts { get; init; } = Array.Empty<PlexPart>();
}

/// <summary>
/// A physical Plex media part.
/// </summary>
public sealed class PlexPart
{
    public int Index { get; init; }
    public string Key { get; init; } = string.Empty;
    public string? Container { get; init; }
    public long? Size { get; init; }
    public long? DurationMs { get; init; }
    public IReadOnlyList<PlexStream> Streams { get; init; } = Array.Empty<PlexStream>();
}

/// <summary>
/// A Plex elementary media stream. Plex uses 1=video, 2=audio, 3=subtitle.
/// </summary>
public sealed class PlexStream
{
    public int Index { get; init; }
    public int StreamType { get; init; }
    public string? Codec { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public string? Key { get; init; }
    public string? Profile { get; init; }
    public string? ScanType { get; init; }
    public string? ChannelLayout { get; init; }
    public int? BitrateKbps { get; init; }
    public int? BitDepth { get; init; }
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public float? FrameRate { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
}

/// <summary>
/// Resolved playable media part.
/// </summary>
public sealed class PlexResolvedPart
{
    public string Key { get; init; } = string.Empty;
    public string? Container { get; init; }
    public long? Size { get; init; }
    public long? DurationMs { get; init; }
    public int? BitrateKbps { get; init; }
}
