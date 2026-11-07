using System.Net.NetworkInformation;
using System.Net.Sockets;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;

namespace VpnMediaSync.Services;

public sealed class VpnGuardService(IOptions<VpnConfig> options, ILogger<VpnGuardService> logger)
{
    private readonly VpnConfig _options = options.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<bool> IsVpnConnectedAsync(CancellationToken ct)
    {
        var requiredInterface = _options.RequiredInterface;

        // Layer 1: Check kernel network interface & assigned IPv4 tunnel address
        var availableNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

        var selectedVpnInterface = availableNetworkInterfaces.FirstOrDefault(i =>
            string.Equals(i.Name, requiredInterface, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Name, "nordlynx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Name, "wg0", StringComparison.OrdinalIgnoreCase));

        if (selectedVpnInterface is null ||
            (selectedVpnInterface.OperationalStatus != OperationalStatus.Up &&
             selectedVpnInterface.OperationalStatus != OperationalStatus.Unknown))
        {
            logger.LogWarning(
                "VPN gate failed: interface '{RequiredInterface}' (or nordlynx/wg0) is missing or not Up.",
                requiredInterface);
            return false;
        }

        var ipv4Addr = selectedVpnInterface.GetIPProperties()
            .UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

        if (ipv4Addr is null)
        {
            logger.LogWarning(
                "VPN gate failed: interface '{InterfaceName}' is Up but has no IPv4 tunnel address.",
                selectedVpnInterface.Name);
            return false;
        }

        // Layer 2: If running against host nordvpn daemon, also verify `nordvpn status`
        if (string.Equals(_options.Provider, "nordvpn-host", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var result = await Cli.Wrap("nordvpn")
                    .WithArguments(["status"])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(ct);

                if (!result.StandardOutput.Contains("Status: Connected", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("VPN gate failed: `nordvpn status` did not report Connected.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to execute `nordvpn status`.");
                return false;
            }
        }

        logger.LogInformation(
            "VPN gate verified: interface '{InterfaceName}' is Up with tunnel IP {TunnelIp}.",
            selectedVpnInterface.Name,
            ipv4Addr.Address);

        return true;
    }
}
