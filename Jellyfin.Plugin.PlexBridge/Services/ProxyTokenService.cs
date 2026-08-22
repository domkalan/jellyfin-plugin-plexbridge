using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.PlexBridge.Configuration;

namespace Jellyfin.Plugin.PlexBridge.Services;

/// <summary>
/// Creates and validates time-limited URLs for the local media and image proxy.
/// Plex credentials never appear in these URLs.
/// </summary>
public sealed class ProxyTokenService
{
    private const string StreamKind = "stream";
    private const string ImageKind = "image";

    public string CreateStreamUrl(string ratingKey, int mediaIndex, int partIndex)
    {
        var resource = $"{ratingKey}:{mediaIndex}:{partIndex}";
        var expiry = GetExpiryUnixSeconds();
        var signature = Sign(StreamKind, resource, expiry);
        var baseUrl = GetInternalBaseUrl();
        return $"{baseUrl}/PlexBridge/stream/{Uri.EscapeDataString(ratingKey)}/{mediaIndex.ToString(CultureInfo.InvariantCulture)}/{partIndex.ToString(CultureInfo.InvariantCulture)}?exp={expiry.ToString(CultureInfo.InvariantCulture)}&sig={signature}";
    }

    public string CreateImageUrl(string plexPath)
    {
        var encodedPath = Base64UrlEncode(Encoding.UTF8.GetBytes(plexPath));
        var expiry = GetExpiryUnixSeconds();
        var signature = Sign(ImageKind, encodedPath, expiry);
        var baseUrl = GetInternalBaseUrl();
        return $"{baseUrl}/PlexBridge/image?p={Uri.EscapeDataString(encodedPath)}&exp={expiry.ToString(CultureInfo.InvariantCulture)}&sig={signature}";
    }

    public bool ValidateStream(string ratingKey, int mediaIndex, int partIndex, long expiry, string signature)
    {
        var resource = $"{ratingKey}:{mediaIndex}:{partIndex}";
        return Validate(StreamKind, resource, expiry, signature);
    }

    public bool TryValidateImage(string encodedPath, long expiry, string signature, out string plexPath)
    {
        plexPath = string.Empty;
        if (!Validate(ImageKind, encodedPath, expiry, signature))
        {
            return false;
        }

        try
        {
            plexPath = Encoding.UTF8.GetString(Base64UrlDecode(encodedPath));
        }
        catch (FormatException)
        {
            return false;
        }

        return IsSafePlexPath(plexPath);
    }

    private static bool IsSafePlexPath(string path)
        => path.StartsWith("/", StringComparison.Ordinal)
            && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.Contains("\\", StringComparison.Ordinal)
            && !path.Contains("\r", StringComparison.Ordinal)
            && !path.Contains("\n", StringComparison.Ordinal);

    private static bool Validate(string kind, string resource, long expiry, string signature)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiry < now - 60 || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var expected = Sign(kind, resource, expiry);
        try
        {
            var expectedBytes = Base64UrlDecode(expected);
            var suppliedBytes = Base64UrlDecode(signature);
            return expectedBytes.Length == suppliedBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Sign(string kind, string resource, long expiry)
    {
        using var hmac = new HMACSHA256(DeriveSigningKey());
        var payload = Encoding.UTF8.GetBytes($"{kind}\n{resource}\n{expiry.ToString(CultureInfo.InvariantCulture)}");
        return Base64UrlEncode(hmac.ComputeHash(payload));
    }

    private static byte[] DeriveSigningKey()
    {
        var token = GetConfiguration().PlexToken ?? string.Empty;
        var input = Encoding.UTF8.GetBytes(token + "|" + Plugin.PluginId.ToString("N", CultureInfo.InvariantCulture));
        return SHA256.HashData(input);
    }

    private static long GetExpiryUnixSeconds()
    {
        var minutes = Math.Clamp(GetConfiguration().SignedUrlLifetimeMinutes, 30, 10080);
        return DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
    }

    private static string GetInternalBaseUrl()
    {
        var url = GetConfiguration().InternalJellyfinUrl?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Plex Bridge Internal Jellyfin URL must be an absolute HTTP or HTTPS URL.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid Base64URL value.")
        };

        return Convert.FromBase64String(normalized);
    }

    private static PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
