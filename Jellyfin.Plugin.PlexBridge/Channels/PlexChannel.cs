using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PlexBridge.Configuration;
using Jellyfin.Plugin.PlexBridge.Models;
using Jellyfin.Plugin.PlexBridge.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlexBridge.Channels;

/// <summary>
/// Exposes selected Plex movie and TV sections as a Jellyfin channel.
/// </summary>
public sealed class PlexChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly PlexClient _plexClient;
    private readonly ProxyTokenService _proxyTokens;
    private readonly ILogger<PlexChannel> _logger;

    public PlexChannel(PlexClient plexClient, ProxyTokenService proxyTokens, ILogger<PlexChannel> logger)
    {
        _plexClient = plexClient;
        _proxyTokens = proxyTokens;
        _logger = logger;
    }

    public string Name => "Plex Bridge";

    public string Description => "Movies and TV shared from a remote Plex Media Server.";

    public string DataVersion => "4";

    public string HomePageUrl => GetConfiguration().PlexServerUrl;

    public ChannelParentalRating ParentalRating => default;

    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            ContentTypes = new List<ChannelMediaContentType>
            {
                ChannelMediaContentType.Movie,
                ChannelMediaContentType.Episode
            },
            MaxPageSize = GetPageSize(),
            SupportsSortOrderToggle = false,
            SupportsContentDownloading = false
        };
    }

    public bool IsEnabledFor(string userId)
    {
        if (!_plexClient.IsConfigured)
        {
            return false;
        }

        var configuredUsers = SplitCsv(GetConfiguration().AllowedUserIds)
            .Select(NormalizeGuidText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return configuredUsers.Count == 0 || configuredUsers.Contains(NormalizeGuidText(userId));
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        if (!_plexClient.IsConfigured)
        {
            return EmptyResult();
        }

        try
        {
            if (string.IsNullOrWhiteSpace(query.FolderId))
            {
                return await GetRootAsync(cancellationToken).ConfigureAwait(false);
            }

            var parts = query.FolderId.Split(':', 3, StringSplitOptions.None);
            if (parts.Length < 2)
            {
                return EmptyResult();
            }

            var start = Math.Max(0, query.StartIndex ?? 0);
            var limit = query.Limit.HasValue
                ? Math.Clamp(query.Limit.Value, 1, GetPageSize())
                : (int?)null;

            return parts[0] switch
            {
                "lib" when parts.Length == 3 => await GetLibraryAsync(parts[1], parts[2], start, limit, cancellationToken).ConfigureAwait(false),
                "show" => await GetChildrenAsync(parts[1], start, limit, cancellationToken).ConfigureAwait(false),
                "season" => await GetChildrenAsync(parts[1], start, limit, cancellationToken).ConfigureAwait(false),
                _ => EmptyResult()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Unable to browse Plex Bridge channel folder {FolderId}", query.FolderId);
            return EmptyResult();
        }
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (!_plexClient.IsConfigured || !TryGetPlayableRatingKey(id, out var ratingKey))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var metadata = await _plexClient.GetMetadataAsync(ratingKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var sources = new List<MediaSourceInfo>();
        foreach (var media in metadata.Media)
        {
            // Plex multi-part titles require playlist/session handling. The current release intentionally exposes
            // only the first part of each media version rather than silently concatenating files.
            var part = media.Parts.FirstOrDefault();
            if (part is null || string.IsNullOrWhiteSpace(part.Key))
            {
                continue;
            }

            var durationMs = part.DurationMs ?? media.DurationMs ?? metadata.DurationMs;
            var mediaStreams = BuildMediaStreams(media, part);
            var defaultAudio = mediaStreams
                .Where(stream => stream.Type == MediaStreamType.Audio)
                .OrderByDescending(stream => stream.IsDefault)
                .Select(stream => (int?)stream.Index)
                .FirstOrDefault();
            var defaultSubtitle = mediaStreams
                .Where(stream => stream.Type == MediaStreamType.Subtitle && stream.IsDefault)
                .Select(stream => (int?)stream.Index)
                .FirstOrDefault();

            var source = new MediaSourceInfo
            {
                Id = CreateMediaSourceId(ratingKey, media.Index, 0),
                Name = BuildMediaName(media),
                Protocol = MediaProtocol.Http,
                Path = _proxyTokens.CreateStreamUrl(ratingKey, media.Index, 0),
                Container = part.Container ?? media.Container,
                Size = part.Size,
                IsRemote = true,
                RunTimeTicks = durationMs.HasValue ? TimeSpan.FromMilliseconds(durationMs.Value).Ticks : null,
                Bitrate = media.BitrateKbps.HasValue ? media.BitrateKbps.Value * 1000 : null,
                MediaStreams = mediaStreams,
                DefaultAudioStreamIndex = defaultAudio,
                DefaultSubtitleStreamIndex = defaultSubtitle,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true
            };

            sources.Add(source);
        }

        return sources;
    }

    private static IReadOnlyList<MediaStream> BuildMediaStreams(PlexMedia media, PlexPart part)
    {
        var streams = new List<MediaStream>();

        foreach (var plexStream in part.Streams)
        {
            // External Plex subtitle streams need their own signed proxy endpoint. Until that
            // endpoint exists, don't advertise them as locally playable tracks to Jellyfin.
            if (plexStream.StreamType == 3 && !string.IsNullOrWhiteSpace(plexStream.Key))
            {
                continue;
            }

            var type = plexStream.StreamType switch
            {
                1 => MediaStreamType.Video,
                2 => MediaStreamType.Audio,
                3 => MediaStreamType.Subtitle,
                _ => (MediaStreamType?)null
            };

            if (!type.HasValue)
            {
                continue;
            }

            streams.Add(new MediaStream
            {
                Type = type.Value,
                Index = plexStream.Index,
                Codec = plexStream.Codec,
                Language = plexStream.Language,
                Title = plexStream.Title,
                Profile = plexStream.Profile,
                BitRate = plexStream.BitrateKbps.HasValue ? plexStream.BitrateKbps.Value * 1000 : null,
                BitDepth = plexStream.BitDepth,
                Channels = plexStream.Channels,
                ChannelLayout = plexStream.ChannelLayout,
                SampleRate = plexStream.SampleRate,
                Width = plexStream.Width,
                Height = plexStream.Height,
                RealFrameRate = plexStream.FrameRate,
                IsInterlaced = string.Equals(plexStream.ScanType, "interlaced", StringComparison.OrdinalIgnoreCase),
                IsDefault = plexStream.IsDefault,
                IsForced = plexStream.IsForced,
                IsExternal = false
            });
        }

        // Some Plex endpoints/servers omit Part/Stream details unless richer metadata flags
        // are requested. Jellyfin's StreamBuilder still needs to know that video/audio exist.
        // These -1 entries intentionally tell Jellyfin/FFmpeg to auto-select the concrete track.
        if (!streams.Any(stream => stream.Type == MediaStreamType.Video))
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = -1,
                Codec = media.VideoCodec,
                Width = media.Width,
                Height = media.Height,
                BitRate = media.BitrateKbps.HasValue ? media.BitrateKbps.Value * 1000 : null,
                IsExternal = false
            });
        }

        if (!streams.Any(stream => stream.Type == MediaStreamType.Audio))
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = -1,
                Codec = media.AudioCodec,
                Channels = media.AudioChannels,
                IsExternal = false
            });
        }

        return streams;
    }

    private static string CreateMediaSourceId(string ratingKey, int mediaIndex, int partIndex)
    {
        // Jellyfin 10.11 parses MediaSourceId as a Guid in its HLS/trickplay path.
        // Use a deterministic GUID so the same Plex version keeps the same ID across
        // callbacks while still allowing multiple Plex versions/parts per item.
        var identity = $"plexbridge\n{ratingKey}\n{mediaIndex.ToString(CultureInfo.InvariantCulture)}\n{partIndex.ToString(CultureInfo.InvariantCulture)}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16)).ToString("D", CultureInfo.InvariantCulture);
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages()
        => Array.Empty<ImageType>();

    private async Task<ChannelItemResult> GetRootAsync(CancellationToken cancellationToken)
    {
        var selected = SplitCsv(GetConfiguration().SelectedLibraryKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var libraries = await _plexClient.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        var items = libraries
            .Where(library => selected.Count == 0 || selected.Contains(library.Key))
            .Select(library => new ChannelItemInfo
            {
                Id = $"lib:{library.Type}:{library.Key}",
                Name = library.Title,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                DateModified = library.UpdatedAt ?? DateTime.UnixEpoch,
                ImageUrl = BuildImageUrl(library.Thumb)
            })
            .ToArray();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Length };
    }

    private async Task<ChannelItemResult> GetLibraryAsync(
        string libraryType,
        string libraryKey,
        int start,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (libraryType is not ("movie" or "show"))
        {
            return EmptyResult();
        }

        if (limit.HasValue)
        {
            var page = await _plexClient.GetLibraryItemsAsync(libraryKey, libraryType, start, limit.Value, cancellationToken).ConfigureAwait(false);
            return new ChannelItemResult
            {
                Items = page.Items.Select(MapItem).ToArray(),
                TotalRecordCount = page.TotalCount
            };
        }

        return await GetAllPagesAsync(
            (offset, pageSize, token) => _plexClient.GetLibraryItemsAsync(libraryKey, libraryType, offset, pageSize, token),
            start,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChannelItemResult> GetChildrenAsync(
        string ratingKey,
        int start,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (limit.HasValue)
        {
            var page = await _plexClient.GetChildrenAsync(ratingKey, start, limit.Value, cancellationToken).ConfigureAwait(false);
            return new ChannelItemResult
            {
                Items = page.Items.Select(MapItem).ToArray(),
                TotalRecordCount = page.TotalCount
            };
        }

        return await GetAllPagesAsync(
            (offset, pageSize, token) => _plexClient.GetChildrenAsync(ratingKey, offset, pageSize, token),
            start,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChannelItemResult> GetAllPagesAsync(
        Func<int, int, CancellationToken, Task<PlexPage>> fetchPage,
        int start,
        CancellationToken cancellationToken)
    {
        var pageSize = GetPageSize();
        var offset = start;
        var totalCount = 0;
        var items = new List<ChannelItemInfo>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchPage(offset, pageSize, cancellationToken).ConfigureAwait(false);
            totalCount = Math.Max(totalCount, page.TotalCount);

            if (page.Items.Count == 0)
            {
                break;
            }

            items.AddRange(page.Items.Select(MapItem));
            offset += page.Items.Count;

            // Plex normally supplies totalSize. Stop when we reach it. If totalSize is
            // absent, a short page is the natural end-of-container signal.
            if ((page.TotalCount > 0 && offset >= page.TotalCount) || page.Items.Count < pageSize)
            {
                break;
            }
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = Math.Max(totalCount, start + items.Count)
        };
    }

    private ChannelItemInfo MapItem(PlexItem item)
    {
        var type = item.Type.ToLowerInvariant();
        var result = new ChannelItemInfo
        {
            Name = item.Title,
            Overview = item.Summary,
            OfficialRating = item.ContentRating,
            CommunityRating = item.Rating,
            ProductionYear = item.Year,
            PremiereDate = item.OriginallyAvailableAt,
            DateCreated = item.AddedAt,
            DateModified = item.UpdatedAt ?? item.AddedAt ?? DateTime.UnixEpoch,
            RunTimeTicks = item.DurationMs.HasValue ? TimeSpan.FromMilliseconds(item.DurationMs.Value).Ticks : null,
            IndexNumber = item.Index,
            ParentIndexNumber = item.ParentIndex,
            SeriesName = item.GrandparentTitle ?? item.ParentTitle,
            ImageUrl = BuildImageUrl(item.Thumb),
            Genres = item.Genres.ToList(),
            Studios = string.IsNullOrWhiteSpace(item.Studio) ? new List<string>() : new List<string> { item.Studio },
            ProviderIds = item.ProviderIds.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            Etag = item.UpdatedAt?.Ticks.ToString(CultureInfo.InvariantCulture)
        };

        switch (type)
        {
            case "movie":
                result.Id = "movie:" + item.RatingKey;
                result.Type = ChannelItemType.Media;
                result.MediaType = ChannelMediaType.Video;
                result.ContentType = ChannelMediaContentType.Movie;
                break;
            case "show":
                result.Id = "show:" + item.RatingKey;
                result.Type = ChannelItemType.Folder;
                result.FolderType = ChannelFolderType.Series;
                break;
            case "season":
                result.Id = "season:" + item.RatingKey;
                result.Type = ChannelItemType.Folder;
                result.FolderType = ChannelFolderType.Season;
                break;
            case "episode":
                result.Id = "episode:" + item.RatingKey;
                result.Type = ChannelItemType.Media;
                result.MediaType = ChannelMediaType.Video;
                result.ContentType = ChannelMediaContentType.Episode;
                break;
            default:
                result.Id = "unknown:" + item.RatingKey;
                result.Type = ChannelItemType.Folder;
                result.FolderType = ChannelFolderType.Container;
                break;
        }

        return result;
    }

    private string? BuildImageUrl(string? plexPath)
    {
        if (!GetConfiguration().EnableImages || string.IsNullOrWhiteSpace(plexPath))
        {
            return null;
        }

        try
        {
            return _proxyTokens.CreateImageUrl(plexPath);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Plex Bridge artwork proxy is not configured yet");
            return null;
        }
    }

    private static string BuildMediaName(PlexMedia media)
    {
        var pieces = new[] { media.VideoResolution, media.VideoCodec, media.AudioCodec, media.Container }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var name = string.Join(" · ", pieces);
        return string.IsNullOrWhiteSpace(name) ? "Plex" : "Plex · " + name;
    }

    private static bool TryGetPlayableRatingKey(string id, out string ratingKey)
    {
        ratingKey = string.Empty;
        var separator = id.IndexOf(':');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        var prefix = id[..separator];
        if (prefix is not ("movie" or "episode"))
        {
            return false;
        }

        ratingKey = id[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(ratingKey);
    }

    private static ChannelItemResult EmptyResult()
        => new() { Items = Array.Empty<ChannelItemInfo>(), TotalRecordCount = 0 };

    private static int GetPageSize()
        => Math.Clamp(GetConfiguration().PageSize, 10, 200);

    private static IEnumerable<string> SplitCsv(string? value)
        => (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item));

    private static string NormalizeGuidText(string? value)
        => (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    private static PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
