namespace Jarvis.Sharp.Security;

using System.Text.RegularExpressions;

public static partial class InputSanitizer
{
    [GeneratedRegex("^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$", RegexOptions.Compiled)]
    private static partial Regex K8sIdentifierRegex();

    [GeneratedRegex(@"^\s*\{[^}]+\}", RegexOptions.Compiled)]
    private static partial Regex LogQlStreamSelectorRegex();

    public static readonly IReadOnlySet<string> DefaultAllowedNamespaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "default",
        "infrastructure",
        "kube-system",
        "media",
        "media-test",
        "monitoring",
        "networking",
        "utilities",
        "utilities-test"
    };

    public static readonly IReadOnlySet<string> DefaultAllowedNodeIps = new HashSet<string>(StringComparer.Ordinal)
    {
        "192.168.1.50",
        "192.168.1.180",
        "192.168.1.185",
        "192.168.1.200",
        "192.168.1.210",
        "192.168.1.211",
        "192.168.1.250"
    };

    public static string ValidateK8sIdentifier(string? value, string fieldName = "identifier")
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(cleaned) || !K8sIdentifierRegex().IsMatch(cleaned))
        {
            throw new ArgumentException(
                $"Invalid Kubernetes {fieldName}: '{value}'. Must contain only lowercase alphanumeric characters, '-' or '.'.",
                nameof(value));
        }
        return cleaned;
    }

    public static string ValidateNamespace(string? ns, IEnumerable<string>? allowedNamespaces = null)
    {
        var cleaned = ValidateK8sIdentifier(ns, "namespace");
        var allowlist = allowedNamespaces != null
            ? new HashSet<string>(allowedNamespaces, StringComparer.Ordinal)
            : DefaultAllowedNamespaces;

        if (!allowlist.Contains(cleaned))
        {
            throw new ArgumentException(
                $"Namespace '{cleaned}' is not in the allowed homelab namespace list: [{string.Join(", ", allowlist)}].",
                nameof(ns));
        }
        return cleaned;
    }

    public static string ValidateNodeIp(string? nodeIp, IEnumerable<string>? allowedIps = null)
    {
        var cleaned = nodeIp?.Trim() ?? string.Empty;
        var allowlist = allowedIps != null
            ? new HashSet<string>(allowedIps, StringComparer.Ordinal)
            : DefaultAllowedNodeIps;

        if (!allowlist.Contains(cleaned))
        {
            throw new ArgumentException(
                $"Node IP '{cleaned}' is not in the allowed homelab SSH node list: [{string.Join(", ", allowlist)}].",
                nameof(nodeIp));
        }
        return cleaned;
    }

    public static string ValidateLogQlQuery(string? query, int maxLength = 1000)
    {
        var cleaned = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(cleaned))
        {
            throw new ArgumentException("LogQL query must not be empty.", nameof(query));
        }
        if (cleaned.Length > maxLength)
        {
            throw new ArgumentException(
                $"LogQL query exceeds maximum allowed length of {maxLength} characters.",
                nameof(query));
        }
        if (!LogQlStreamSelectorRegex().IsMatch(cleaned))
        {
            throw new ArgumentException(
                $"LogQL query must begin with a stream selector (e.g. '{{namespace=\"monitoring\"}}'): '{query}'",
                nameof(query));
        }
        if (cleaned.Contains('\n') || cleaned.Contains('\r'))
        {
            throw new ArgumentException("LogQL query must not contain newline characters.", nameof(query));
        }
        return cleaned;
    }
}
