using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace UniFlow.WorkListCleaner.Services;

public class AccessAgentClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AccessAgentClient> _logger;
    private readonly string _baseUrl;

    public AccessAgentClient(ILogger<AccessAgentClient> logger, string agentUrl)
    {
        _logger = logger;
        _baseUrl = agentUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<List<Dictionary<string, object?>>> GetRecordsAsync(string table, string? where = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/records/{table}";
        if (!string.IsNullOrEmpty(where)) url += $"?where={Uri.EscapeDataString(where)}";

        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = await _http.GetFromJsonAsync<RecordsResponse>(url, opts, ct);
            return response?.rows ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent GET failed: {Msg}", ex.Message);
            return new();
        }
    }

    public async Task<int> UpdateAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var req = new UpdateRequest { Sql = sql, Parameters = parameters };
            var response = await _http.PostAsJsonAsync($"{_baseUrl}/update", req, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<UpdateResponse>(opts, ct);
            return result?.affected ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent POST failed: {Msg}", ex.Message);
            return 0;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var resp = await _http.GetFromJsonAsync<HealthResponse>($"{_baseUrl}/health", opts, ct);
            return resp?.status == "ok";
        }
        catch { return false; }
    }

    public class RecordsResponse
    {
        public string? table { get; set; }
        public int count { get; set; }
        public List<Dictionary<string, object?>>? rows { get; set; }
    }

    public class UpdateResponse
    {
        public int affected { get; set; }
    }

    public class HealthResponse
    {
        public string? status { get; set; }
        public bool db { get; set; }
    }

    public class UpdateRequest
    {
        public string? Sql { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
    }
}