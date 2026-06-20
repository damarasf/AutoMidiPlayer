using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// MIDI source backed by the BitMidi online library (https://bitmidi.com).
/// </summary>
public sealed class BitMidiSource(HttpClient client) : IMidiSource
{
    private const string BaseUrl = "https://bitmidi.com";

    public string Name => "BitMidi";

    public async Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        // An empty query browses the whole library; a non-empty one searches.
        // Sending an empty "q=" returns nothing, so the parameter must be omitted entirely.
        var url = string.IsNullOrWhiteSpace(query)
            ? $"{BaseUrl}/api/midi/search?page={page}"
            : $"{BaseUrl}/api/midi/search?q={Uri.EscapeDataString(query)}&page={page}";

        var response = await client.GetFromJsonAsync<ApiResponse>(url, cancellationToken);
        var result = response?.Result;

        if (result?.Results is null)
            return new OnlineMidiSearchPage(Array.Empty<OnlineMidiItem>(), page, 0, 0);

        var items = result.Results
            .Where(item => !string.IsNullOrWhiteSpace(item.DownloadUrl))
            .Select(item => new OnlineMidiItem(
                item.Id.ToString(),
                item.Name ?? "Unknown",
                item.DownloadUrl!,
                $"{item.Views:N0} views • {item.Plays:N0} plays"))
            .ToList();

        return new OnlineMidiSearchPage(items, result.Page <= 0 ? page : result.Page, result.PageTotal, result.Total);
    }

    public async Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken)
    {
        var downloadUrl = item.Reference.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? item.Reference
            : BaseUrl + item.Reference;

        var bytes = await client.GetByteArrayAsync(downloadUrl, cancellationToken);

        return new[] { new DownloadedMidi(item.Name, bytes) };
    }

    private sealed class ApiResponse
    {
        [JsonPropertyName("result")]
        public ApiResult? Result { get; set; }
    }

    private sealed class ApiResult
    {
        [JsonPropertyName("results")]
        public List<ApiMidi>? Results { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("pageTotal")]
        public int PageTotal { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }
    }

    private sealed class ApiMidi
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("views")]
        public int Views { get; set; }

        [JsonPropertyName("plays")]
        public int Plays { get; set; }
    }
}
