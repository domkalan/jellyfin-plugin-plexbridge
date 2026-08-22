using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.PlexBridge.Configuration;
using Jellyfin.Plugin.PlexBridge.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlexBridge.Services;

/// <summary>
/// Minimal Plex Media Server client used for library browsing and secure proxying.
/// </summary>
public sealed class PlexClient
{
    private const string ClientIdentifier = "jellyfin-plexbridge-978ac9fe8d184d26a4320d3150143c98";
    private readonly HttpClient _httpClient;
    private readonly ILogger<PlexClient> _logger;
    private readonly ConcurrentDictionary<string, ResolvedCacheEntry> _partCache = new(StringComparer.Ordinal);

    public PlexClient(HttpClient httpClient, ILogger<PlexClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the minimum connection settings are present.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var config = GetConfiguration();
            return config.Enabled
                && Uri.TryCreate(config.PlexServerUrl, UriKind.Absolute, out _)
                && !string.IsNullOrWhiteSpace(config.PlexToken);
        }
    }

    public async Task<IReadOnlyList<PlexLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        var document = await GetXmlAsync("/library/sections", null, null, cancellationToken).ConfigureAwait(false);
        return document.Root?
            .Elements("Directory")
            .Select(ParseLibrary)
            .Where(library => library.Type is "movie" or "show")
            .ToArray()
            ?? Array.Empty<PlexLibrary>();
    }

    public Task<PlexPage> GetLibraryItemsAsync(
        string sectionKey,
        string libraryType,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        var plexType = string.Equals(libraryType, "movie", StringComparison.OrdinalIgnoreCase) ? "1" : "2";
        var path = $"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={plexType}&includeGuids=1";
        return GetPageAsync(path, startIndex, limit, cancellationToken);
    }

    public Task<PlexPage> GetChildrenAsync(
        string ratingKey,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        var path = $"/library/metadata/{Uri.EscapeDataString(ratingKey)}/children?includeGuids=1";
        return GetPageAsync(path, startIndex, limit, cancellationToken);
    }

    public async Task<PlexItem?> GetMetadataAsync(string ratingKey, CancellationToken cancellationToken)
    {
        var path = $"/library/metadata/{Uri.EscapeDataString(ratingKey)}?includeGuids=1";
        var document = await GetXmlAsync(path, null, null, cancellationToken).ConfigureAwait(false);
        var element = document.Root?.Elements().FirstOrDefault(IsMetadataElement);
        return element is null ? null : ParseItem(element);
    }

    public async Task<PlexResolvedPart?> ResolvePartAsync(
        string ratingKey,
        int mediaIndex,
        int partIndex,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{ratingKey}:{mediaIndex}:{partIndex}";
        if (_partCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Part;
        }

        var metadata = await GetMetadataAsync(ratingKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var media = metadata.Media.FirstOrDefault(item => item.Index == mediaIndex)
            ?? metadata.Media.ElementAtOrDefault(mediaIndex);
        var part = media?.Parts.FirstOrDefault(item => item.Index == partIndex)
            ?? media?.Parts.ElementAtOrDefault(partIndex);

        if (media is null || part is null || string.IsNullOrWhiteSpace(part.Key))
        {
            return null;
        }

        var resolved = new PlexResolvedPart
        {
            Key = part.Key,
            Container = part.Container ?? media.Container,
            Size = part.Size,
            DurationMs = part.DurationMs ?? media.DurationMs ?? metadata.DurationMs,
            BitrateKbps = media.BitrateKbps
        };

        _partCache[cacheKey] = new ResolvedCacheEntry(resolved, DateTimeOffset.UtcNow.AddMinutes(10));
        return resolved;
    }

    public Task<HttpResponseMessage> OpenMediaAsync(
        string plexPath,
        HttpMethod method,
        string? range,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, plexPath);
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public Task<HttpResponseMessage> OpenImageAsync(string plexPath, CancellationToken cancellationToken)
    {
        var request = CreateRequest(HttpMethod.Get, plexPath);
        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<PlexPage> GetPageAsync(string path, int startIndex, int limit, CancellationToken cancellationToken)
    {
        var document = await GetXmlAsync(path, startIndex, limit, cancellationToken).ConfigureAwait(false);
        var root = document.Root;
        if (root is null)
        {
            return new PlexPage();
        }

        var items = root.Elements().Where(IsMetadataElement).Select(ParseItem).ToArray();
        var total = AttributeInt(root, "totalSize") ?? AttributeInt(root, "size") ?? items.Length;
        return new PlexPage { Items = items, TotalCount = total };
    }

    private async Task<XDocument> GetXmlAsync(
        string path,
        int? startIndex,
        int? limit,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        if (startIndex.HasValue)
        {
            request.Headers.TryAddWithoutValidation("X-Plex-Container-Start", startIndex.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (limit.HasValue)
        {
            request.Headers.TryAddWithoutValidation("X-Plex-Container-Size", limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Plex request {Path} failed with status {StatusCode}", path, (int)response.StatusCode);
            throw new HttpRequestException($"Plex returned HTTP {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var config = GetConfiguration();
        if (!Uri.TryCreate(config.PlexServerUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Plex Bridge is not configured with a valid Plex server URL.");
        }

        if (string.IsNullOrWhiteSpace(config.PlexToken))
        {
            throw new InvalidOperationException("Plex Bridge is not configured with a Plex token.");
        }

        var normalizedBase = baseUri.ToString().TrimEnd('/') + "/";
        var normalizedPath = path.TrimStart('/');
        var uri = new Uri(normalizedBase + normalizedPath, UriKind.Absolute);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/xml,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", config.PlexToken);
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientIdentifier);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "Jellyfin Plex Bridge");
        request.Headers.TryAddWithoutValidation("X-Plex-Version", typeof(PlexClient).Assembly.GetName().Version?.ToString() ?? "unknown");
        request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Jellyfin");
        return request;
    }

    private static PlexLibrary ParseLibrary(XElement element)
    {
        return new PlexLibrary
        {
            Key = Attribute(element, "key"),
            Title = Attribute(element, "title"),
            Type = Attribute(element, "type"),
            Thumb = AttributeOrNull(element, "thumb"),
            UpdatedAt = UnixDate(element, "updatedAt")
        };
    }

    private static PlexItem ParseItem(XElement element)
    {
        var media = element.Elements("Media").Select((mediaElement, mediaIndex) =>
        {
            var parts = mediaElement.Elements("Part").Select((partElement, partIndex) =>
            {
                var streams = partElement.Elements("Stream").Select((streamElement, streamOrdinal) => new PlexStream
                {
                    Index = AttributeInt(streamElement, "index") ?? streamOrdinal,
                    StreamType = AttributeInt(streamElement, "streamType") ?? AttributeInt(streamElement, "type") ?? 0,
                    Codec = AttributeOrNull(streamElement, "codec"),
                    Language = AttributeOrNull(streamElement, "languageCode") ?? AttributeOrNull(streamElement, "language"),
                    Title = AttributeOrNull(streamElement, "title"),
                    Key = AttributeOrNull(streamElement, "key"),
                    Profile = AttributeOrNull(streamElement, "profile"),
                    ScanType = AttributeOrNull(streamElement, "scanType"),
                    ChannelLayout = AttributeOrNull(streamElement, "audioChannelLayout"),
                    BitrateKbps = AttributeInt(streamElement, "bitrate"),
                    BitDepth = AttributeInt(streamElement, "bitDepth"),
                    Channels = AttributeInt(streamElement, "channels"),
                    SampleRate = AttributeInt(streamElement, "samplingRate"),
                    Width = AttributeInt(streamElement, "width"),
                    Height = AttributeInt(streamElement, "height"),
                    FrameRate = AttributeFloat(streamElement, "frameRate"),
                    IsDefault = AttributeBool(streamElement, "default"),
                    IsForced = AttributeBool(streamElement, "forced")
                }).ToArray();

                return new PlexPart
                {
                    Index = partIndex,
                    Key = Attribute(partElement, "key"),
                    Container = AttributeOrNull(partElement, "container"),
                    Size = AttributeLong(partElement, "size"),
                    DurationMs = AttributeLong(partElement, "duration"),
                    Streams = streams
                };
            }).ToArray();

            return new PlexMedia
            {
                Index = mediaIndex,
                Container = AttributeOrNull(mediaElement, "container"),
                VideoResolution = AttributeOrNull(mediaElement, "videoResolution"),
                VideoCodec = AttributeOrNull(mediaElement, "videoCodec"),
                AudioCodec = AttributeOrNull(mediaElement, "audioCodec"),
                BitrateKbps = AttributeInt(mediaElement, "bitrate"),
                Width = AttributeInt(mediaElement, "width"),
                Height = AttributeInt(mediaElement, "height"),
                AudioChannels = AttributeInt(mediaElement, "audioChannels"),
                DurationMs = AttributeLong(mediaElement, "duration"),
                Parts = parts
            };
        }).ToArray();

        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guidElement in element.Elements("Guid"))
        {
            var raw = AttributeOrNull(guidElement, "id");
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var separator = raw.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var provider = raw[..separator].ToLowerInvariant();
            var value = raw[(separator + 3)..];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var key = provider switch
            {
                "imdb" => "Imdb",
                "tmdb" => "Tmdb",
                "tvdb" => "Tvdb",
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(key))
            {
                providerIds[key] = value;
            }
        }

        return new PlexItem
        {
            RatingKey = Attribute(element, "ratingKey"),
            Type = Attribute(element, "type"),
            Title = Attribute(element, "title"),
            Summary = AttributeOrNull(element, "summary"),
            ContentRating = AttributeOrNull(element, "contentRating"),
            Studio = AttributeOrNull(element, "studio"),
            Thumb = AttributeOrNull(element, "thumb"),
            Art = AttributeOrNull(element, "art"),
            ParentTitle = AttributeOrNull(element, "parentTitle"),
            GrandparentTitle = AttributeOrNull(element, "grandparentTitle"),
            Year = AttributeInt(element, "year"),
            Index = AttributeInt(element, "index"),
            ParentIndex = AttributeInt(element, "parentIndex"),
            DurationMs = AttributeLong(element, "duration"),
            Rating = AttributeFloat(element, "rating"),
            OriginallyAvailableAt = DateAttribute(element, "originallyAvailableAt"),
            AddedAt = UnixDate(element, "addedAt"),
            UpdatedAt = UnixDate(element, "updatedAt"),
            Genres = element.Elements("Genre")
                .Select(genre => AttributeOrNull(genre, "tag"))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Cast<string>()
                .ToArray(),
            ProviderIds = providerIds,
            Media = media
        };
    }

    private static bool IsMetadataElement(XElement element)
        => element.Name.LocalName is "Video" or "Directory" or "Track";

    private static string Attribute(XElement element, string name)
        => element.Attribute(name)?.Value ?? string.Empty;

    private static string? AttributeOrNull(XElement element, string name)
        => element.Attribute(name)?.Value;

    private static int? AttributeInt(XElement element, string name)
        => int.TryParse(AttributeOrNull(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? AttributeLong(XElement element, string name)
        => long.TryParse(AttributeOrNull(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static float? AttributeFloat(XElement element, string name)
        => float.TryParse(AttributeOrNull(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool AttributeBool(XElement element, string name)
    {
        var raw = AttributeOrNull(element, name);
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? DateAttribute(XElement element, string name)
        => DateTime.TryParse(AttributeOrNull(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : null;

    private static DateTime? UnixDate(XElement element, string name)
    {
        var seconds = AttributeLong(element, name);
        if (!seconds.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private sealed record ResolvedCacheEntry(PlexResolvedPart Part, DateTimeOffset ExpiresAt);
}
