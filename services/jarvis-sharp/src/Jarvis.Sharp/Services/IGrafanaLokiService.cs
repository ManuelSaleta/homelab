namespace Jarvis.Sharp.Services;

using System.Net.Http.Headers;
using System.Text.Json;
using Jarvis.Sharp.Security;

public record LokiLogEntry(string Timestamp, string Message, IReadOnlyDictionary<string, string> Labels);

public record LokiQueryResult(
    string Status,
    int TotalLines,
    string Query,
    IReadOnlyList<LokiLogEntry> Lines
);

public interface IGrafanaLokiService
{
    Task<LokiQueryResult> QueryLogsAsync(
        string query,
        string? startTime = null,
        string? endTime = null,
        int limit = 100,
        string? grafanaUrl = null,
        string? grafanaToken = null,
        string? datasourceUid = null,
        CancellationToken cancellationToken = default);
}

public class GrafanaLokiService : IGrafanaLokiService
{
    private readonly HttpClient _httpClient;

    public GrafanaLokiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<LokiQueryResult> QueryLogsAsync(
        string query,
        string? startTime = null,
        string? endTime = null,
        int limit = 100,
        string? grafanaUrl = null,
        string? grafanaToken = null,
        string? datasourceUid = null,
        CancellationToken cancellationToken = default)
    {
        var cleanQuery = InputSanitizer.ValidateLogQlQuery(query);
        var safeLimit = Math.Clamp(limit, 1, 1000);

        var baseUrl = (grafanaUrl ?? Environment.GetEnvironmentVariable("GRAFANA_URL") ?? "https://grafana.freesalty.com").TrimEnd('/');
        var token = (grafanaToken ?? Environment.GetEnvironmentVariable("GRAFANA_TOKEN") ?? string.Empty).Trim();
        var dsUid = (datasourceUid ?? Environment.GetEnvironmentVariable("GRAFANA_LOKI_DATASOURCE_UID") ?? "loki").Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("GRAFANA_URL must not be empty.", nameof(grafanaUrl));
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("GRAFANA_TOKEN is required for authenticated log querying.", nameof(grafanaToken));
        }

        var endpoint = $"{baseUrl}/api/datasources/proxy/uid/{dsUid}/loki/api/v1/query_range?query={Uri.EscapeDataString(cleanQuery)}&limit={safeLimit}&direction=backward";
        if (!string.IsNullOrWhiteSpace(startTime))
        {
            endpoint += $"&start={Uri.EscapeDataString(startTime.Trim())}";
        }
        if (!string.IsNullOrWhiteSpace(endTime))
        {
            endpoint += $"&end={Uri.EscapeDataString(endTime.Trim())}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HomelabChatOps/1.0)");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
        var entries = new List<LokiLogEntry>();

        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("result", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var streamElem in results.EnumerateArray())
            {
                var labelMap = new Dictionary<string, string>(StringComparer.Ordinal);
                if (streamElem.TryGetProperty("stream", out var streamObj) && streamObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in streamObj.EnumerateObject())
                    {
                        labelMap[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                if (streamElem.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var valArray in values.EnumerateArray())
                    {
                        if (valArray.ValueKind == JsonValueKind.Array && valArray.GetArrayLength() >= 2)
                        {
                            var ts = valArray[0].GetString() ?? string.Empty;
                            var rawMsg = valArray[1].GetString() ?? string.Empty;
                            entries.Add(new LokiLogEntry(ts, rawMsg.TrimEnd('\r', '\n'), labelMap));
                        }
                    }
                }
            }
        }

        // Sort descending by timestamp
        entries.Sort((a, b) => string.CompareOrdinal(b.Timestamp, a.Timestamp));

        return new LokiQueryResult(
            status,
            entries.Count,
            cleanQuery,
            entries.Take(safeLimit).ToList().AsReadOnly()
        );
    }
}
