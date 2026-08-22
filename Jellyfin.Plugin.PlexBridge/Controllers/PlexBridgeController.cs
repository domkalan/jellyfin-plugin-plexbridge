using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PlexBridge.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlexBridge.Controllers;

/// <summary>
/// Server-side authenticated proxy. The URL is signed, so FFmpeg can access it without exposing the Plex token.
/// </summary>
[ApiController]
[Route("PlexBridge")]
[AllowAnonymous]
public sealed class PlexBridgeController : ControllerBase
{
    private readonly PlexClient _plexClient;
    private readonly ProxyTokenService _tokens;
    private readonly ILogger<PlexBridgeController> _logger;

    public PlexBridgeController(PlexClient plexClient, ProxyTokenService tokens, ILogger<PlexBridgeController> logger)
    {
        _plexClient = plexClient;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>
    /// Proxies a Plex media part and preserves byte range semantics for seeking.
    /// </summary>
    [AcceptVerbs("GET", "HEAD")]
    [Route("stream/{ratingKey}/{mediaIndex:int}/{partIndex:int}")]
    public async Task Stream(
        string ratingKey,
        int mediaIndex,
        int partIndex,
        [FromQuery] long exp,
        [FromQuery] string sig,
        CancellationToken cancellationToken)
    {
        if (!_tokens.ValidateStream(ratingKey, mediaIndex, partIndex, exp, sig))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            var resolved = await _plexClient.ResolvePartAsync(ratingKey, mediaIndex, partIndex, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
            var range = Request.Headers["Range"].ToString();
            using var upstream = await _plexClient.OpenMediaAsync(resolved.Key, method, range, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Plex Bridge stream {RatingKey}/{MediaIndex}/{PartIndex}: Plex HTTP {StatusCode}, range {Range}, type {ContentType}, length {ContentLength}",
                ratingKey,
                mediaIndex,
                partIndex,
                (int)upstream.StatusCode,
                string.IsNullOrWhiteSpace(range) ? "<none>" : range,
                upstream.Content.Headers.ContentType?.ToString() ?? "<unknown>",
                upstream.Content.Headers.ContentLength?.ToString() ?? "<unknown>");

            if (!upstream.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Plex refused media part {RatingKey}/{MediaIndex}/{PartIndex} with HTTP {StatusCode}. A Plex playback decision/transcode session may be required.",
                    ratingKey,
                    mediaIndex,
                    partIndex,
                    (int)upstream.StatusCode);
            }

            await RelayAsync(upstream, includeBody: method != HttpMethod.Head, isImage: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected or Jellyfin cancelled the transcode/probe.
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Plex Bridge media proxy failed for rating key {RatingKey}", ratingKey);
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    /// <summary>
    /// Proxies Plex artwork without exposing X-Plex-Token in Jellyfin metadata.
    /// </summary>
    [HttpGet("image")]
    public async Task Image(
        [FromQuery(Name = "p")] string encodedPath,
        [FromQuery] long exp,
        [FromQuery] string sig,
        CancellationToken cancellationToken)
    {
        if (!_tokens.TryValidateImage(encodedPath, exp, sig, out var plexPath))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            using var upstream = await _plexClient.OpenImageAsync(plexPath, cancellationToken).ConfigureAwait(false);
            await RelayAsync(upstream, includeBody: true, isImage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Plex Bridge image proxy failed");
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private async Task RelayAsync(
        HttpResponseMessage upstream,
        bool includeBody,
        bool isImage,
        CancellationToken cancellationToken)
    {
        Response.StatusCode = (int)upstream.StatusCode;

        if (upstream.Content.Headers.ContentType is not null)
        {
            Response.ContentType = upstream.Content.Headers.ContentType.ToString();
        }

        if (upstream.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
        }

        CopyHeader(upstream, "Accept-Ranges");
        CopyHeader(upstream, "ETag");
        CopyContentHeader(upstream, "Content-Range");
        CopyContentHeader(upstream, "Content-Disposition");
        CopyContentHeader(upstream, "Last-Modified");

        Response.Headers["Cache-Control"] = isImage ? "private, max-age=3600" : "no-store";
        Response.Headers.Remove("transfer-encoding");

        if (includeBody)
        {
            await upstream.Content.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CopyHeader(HttpResponseMessage upstream, string name)
    {
        if (upstream.Headers.TryGetValues(name, out var values))
        {
            Response.Headers[name] = string.Join(", ", values);
        }
    }

    private void CopyContentHeader(HttpResponseMessage upstream, string name)
    {
        if (upstream.Content.Headers.TryGetValues(name, out var values))
        {
            Response.Headers[name] = string.Join(", ", values);
        }
    }
}
