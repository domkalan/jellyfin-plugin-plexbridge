using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PlexBridge.Configuration;

/// <summary>
/// Plex Bridge persisted configuration.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the configured Plex servers.</summary>
    public System.Collections.Generic.List<PlexServerConfiguration> Servers { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the channel is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the Plex Media Server base URL.
    /// </summary>
    public string PlexServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Plex access token. It is used server-side only.
    /// </summary>
    public string PlexToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin URL that the Jellyfin server itself can reach.
    /// Usually http://127.0.0.1:8096, including Base URL if configured.
    /// </summary>
    public string InternalJellyfinUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>
    /// Gets or sets a comma-separated list of Plex library section keys to expose.
    /// Empty means all movie and show sections.
    /// </summary>
    public string SelectedLibraryKeys { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a comma-separated list of Jellyfin user GUIDs allowed to see the channel.
    /// Empty means all Jellyfin users.
    /// </summary>
    public string AllowedUserIds { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum page size used when browsing Plex.
    /// </summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether Plex artwork should be proxied into Jellyfin.
    /// </summary>
    public bool EnableImages { get; set; } = true;

    /// <summary>
    /// Gets or sets the signed media URL lifetime in minutes.
    /// </summary>
    public int SignedUrlLifetimeMinutes { get; set; } = 1440;

    /// <summary>Returns configured servers, including a legacy single-server migration view.</summary>
    public System.Collections.Generic.IReadOnlyList<PlexServerConfiguration> GetEffectiveServers()
    {
        if (Servers.Count > 0)
        {
            return Servers;
        }

        if (string.IsNullOrWhiteSpace(PlexServerUrl) || string.IsNullOrWhiteSpace(PlexToken))
        {
            return System.Array.Empty<PlexServerConfiguration>();
        }

        return new[]
        {
            new PlexServerConfiguration
            {
                Id = "legacy",
                Name = "Plex Server",
                Url = PlexServerUrl,
                Token = PlexToken,
                Enabled = Enabled,
                SelectedLibraryKeys = SelectedLibraryKeys
            }
        };
    }
}
