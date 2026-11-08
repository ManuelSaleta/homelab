namespace VpnMediaSync.Models.Options;

public sealed class VpnConfigOpts
{
    public const string SectionName = "Vpn";

    /// <summary>
    /// VPN implementation provider or management method (e.g., 'nordlynx', 'nordvpn-host', or 'wireguard').
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The default virtual network interface created by the WireGuard VPN protocol.
    /// </summary>
    public string RequiredInterface { get; set; } = string.Empty;

    /// <summary>
    /// Flag indicating whether active torrents should be paused if the VPN tunnel is detected down.
    /// </summary>
    public bool PauseTorrentsWhenDisconnected { get; set; }
}
