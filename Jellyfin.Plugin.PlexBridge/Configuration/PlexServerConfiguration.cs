using System;

namespace Jellyfin.Plugin.PlexBridge.Configuration;

/// <summary>
/// Configuration for one Plex Media Server.
/// </summary>
public sealed class PlexServerConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string SelectedLibraryKeys { get; set; } = string.Empty;
}
