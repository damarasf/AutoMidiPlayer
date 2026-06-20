using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// MIDI source backed by the Internet Archive (https://archive.org).
/// A search result is an archive item that may bundle several MIDI files.
/// </summary>
public sealed class ArchiveOrgSource(HttpClient client) : IMidiSource
{
    private const string BaseUrl = "https://archive.org";
    private const int PageSize = 20;

    /// <summary>Maximum number of MIDI files pulled from a single archive item per download.</summary>
    private const int MaxFilesPerItem = 30;

    public string Name => "Internet Archive";

    public async Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        var q = Uri.EscapeDataString($"format:(MIDI) AND ({query})");
        var url = $"{BaseUrl}/advancedsearch.php?q={q}&fl[]=identifier&fl[]=title&rows={PageSize}&page={page}&output=json";

        var response = await client.GetFromJsonAsync<ApiResponse>(url, cancellationToken);
        var docs = response?.Response?.Docs;

        if (docs is null)
            return new OnlineMidiSearchPage(Array.Empty<OnlineMidiItem>(), page, 0, 0);

        var items = docs
            .Where(doc => !string.IsNullOrWhiteSpace(doc.Identifier))
            .Select(doc => new OnlineMidiItem(
                doc.Identifier!,
                doc.Title ?? doc.Identifier!,
                doc.Identifier!,
                "MIDI collection"))
            .ToList();

        var total = response?.Response?.NumFound ?? items.Count;
        var pageTotal = (int)Math.Ceiling(total / (double)PageSize);

        return new OnlineMidiSearchPage(items, page, pageTotal, total);
    }

    public async Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken)
    {
        var identifier = item.Reference;
        var metadata = await client.GetFromJsonAsync<MetadataResponse>($"{BaseUrl}/metadata/{identifier}", cancellationToken);

        var midiFiles = (metadata?.Files ?? new List<MetadataFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.Name) && IsMidi(file))
            .Take(MaxFilesPerItem)
            .ToList();

        var downloaded = new List<DownloadedMidi>();

        foreach (var file in midiFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileUrl = $"{BaseUrl}/download/{identifier}/{Uri.EscapeDataString(file.Name!)}";

            try
            {
                var bytes = await client.GetByteArrayAsync(fileUrl, cancellationToken);
                downloaded.Add(new DownloadedMidi(file.Name!, bytes));
            }
            catch (HttpRequestException)
            {
                // Skip individual files that fail; the orchestrator validates the rest.
            }
        }

        return downloaded;
    }

    private static bool IsMidi(MetadataFile file) =>
        string.Equals(file.Format, "MIDI", StringComparison.OrdinalIgnoreCase)
        || file.Name!.EndsWith(".mid", StringComparison.OrdinalIgnoreCase)
        || file.Name!.EndsWith(".midi", StringComparison.OrdinalIgnoreCase);

    private sealed class ApiResponse
    {
        [JsonPropertyName("response")]
        public ApiResponseBody? Response { get; set; }
    }

    private sealed class ApiResponseBody
    {
        [JsonPropertyName("numFound")]
        public int NumFound { get; set; }

        [JsonPropertyName("docs")]
        public List<ApiDoc>? Docs { get; set; }
    }

    private sealed class ApiDoc
    {
        [JsonPropertyName("identifier")]
        public string? Identifier { get; set; }

        [JsonPropertyName("title")]
        [JsonConverter(typeof(StringOrArrayConverter))]
        public string? Title { get; set; }
    }

    /// <summary>
    /// Reads an Internet Archive metadata field that may be a single string or an array of strings.
    /// </summary>
    private sealed class StringOrArrayConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.StartArray:
                    string? first = null;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String && first is null)
                            first = reader.GetString();
                    }
                    return first;
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed class MetadataResponse
    {
        [JsonPropertyName("files")]
        public List<MetadataFile>? Files { get; set; }
    }

    private sealed class MetadataFile
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }
    }
}
