using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PlexBridge.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlexBridge.Services;

/// <summary>
/// Bridges Jellyfin playback events to Plex timeline notifications.
/// </summary>
public sealed class PlexPlaybackReporter : IHostedService
{
    private static readonly TimeSpan MinimumReportInterval = TimeSpan.FromSeconds(8);
    private readonly ISessionManager _sessionManager;
    private readonly PlexClient _plexClient;
    private readonly ILogger<PlexPlaybackReporter> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastReports = new(StringComparer.Ordinal);

    public PlexPlaybackReporter(
        ISessionManager sessionManager,
        PlexClient plexClient,
        ILogger<PlexPlaybackReporter> logger)
    {
        _sessionManager = sessionManager;
        _plexClient = plexClient;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        await ReportAsync(eventArgs, "playing", force: true).ConfigureAwait(false);
    }

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        await ReportAsync(eventArgs, eventArgs.IsPaused ? "paused" : "playing", force: false).ConfigureAwait(false);
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs eventArgs)
    {
        try
        {
            await ReportAsync(eventArgs, "stopped", force: true).ConfigureAwait(false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.SessionId))
            {
                _lastReports.TryRemove(eventArgs.SessionId, out _);
            }
        }
    }

    private async Task ReportAsync(PlaybackProgressEventArgs eventArgs, string state, bool force)
    {
        if (!TryGetPlayback(eventArgs.MediaSource?.Path, eventArgs.SessionId, eventArgs.Item?.RunTimeTicks, eventArgs.PlaybackPositionTicks, out var context))
        {
            return;
        }

        await ReportAsync(context, state, force).ConfigureAwait(false);
    }

    private async Task ReportAsync(PlaybackStopEventArgs eventArgs, string state, bool force)
    {
        if (!TryGetPlayback(eventArgs.MediaSource?.Path, eventArgs.SessionId, eventArgs.Item?.RunTimeTicks, eventArgs.PlaybackPositionTicks, out var context))
        {
            return;
        }

        await ReportAsync(context, state, force).ConfigureAwait(false);
    }

    private async Task ReportAsync(PlaybackContext context, string state, bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && _lastReports.TryGetValue(context.SessionId, out var last) && now - last < MinimumReportInterval)
        {
            return;
        }

        var server = GetConfiguration().GetEffectiveServers().FirstOrDefault(item =>
            item.Enabled
            && item.ReportPlayback
            && string.Equals(item.Id, context.ServerId, StringComparison.OrdinalIgnoreCase));
        if (server is null || !_plexClient.IsConfigured(server))
        {
            return;
        }

        _lastReports[context.SessionId] = now;
        try
        {
            var success = await _plexClient.ReportTimelineAsync(
                server,
                context.RatingKey,
                state,
                context.PositionMs,
                context.DurationMs,
                context.SessionId,
                "Jellyfin",
                CancellationToken.None).ConfigureAwait(false);

            if (!success)
            {
                _logger.LogDebug("Plex playback report for server {ServerId} returned an unsuccessful response", context.ServerId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Unable to report Jellyfin playback to Plex server {ServerId}", context.ServerId);
        }
    }

    private static bool TryGetPlayback(
        string? mediaPath,
        string? sessionId,
        long? runtimeTicks,
        long? positionTicks,
        out PlaybackContext context)
    {
        context = default;
        if (string.IsNullOrWhiteSpace(mediaPath) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var streamIndex = Array.FindIndex(segments, value => string.Equals(value, "stream", StringComparison.OrdinalIgnoreCase));
        if (streamIndex < 0 || segments.Length <= streamIndex + 2)
        {
            return false;
        }

        var serverId = Uri.UnescapeDataString(segments[streamIndex + 1]);
        var ratingKey = Uri.UnescapeDataString(segments[streamIndex + 2]);
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(ratingKey))
        {
            return false;
        }

        context = new PlaybackContext(
            serverId,
            ratingKey,
            sessionId,
            Math.Max(0, (positionTicks ?? 0) / TimeSpan.TicksPerMillisecond),
            Math.Max(0, (runtimeTicks ?? 0) / TimeSpan.TicksPerMillisecond));
        return true;
    }

    private static PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private readonly record struct PlaybackContext(
        string ServerId,
        string RatingKey,
        string SessionId,
        long PositionMs,
        long DurationMs);
}
