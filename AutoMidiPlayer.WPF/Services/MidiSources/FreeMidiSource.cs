using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// MIDI source backed by FreeMidi (https://freemidi.org).
/// FreeMidi has no public API, so results are scraped from the search page and
/// downloads use its two-step flow: visit the song page (to establish a session)
/// then fetch the "getter" endpoint.
/// </summary>
public sealed class FreeMidiSource(HttpClient client) : IMidiSource
{
    private const string BaseUrl = "https://freemidi.org";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // <h5 class=card-title><a href=download3-8364-some-slug title="Some Title">...
    private static readonly Regex ResultRegex = new(
        "href=\"?/?(?<ref>download3-(?<id>\\d+)-[^\"\\s>]+)\"?[^>]*?title=\"(?<title>[^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GetterRegex = new(
        "getter-(?<id>\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "FreeMidi";

    public async Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        // FreeMidi's search returns a single, non-paginated result set.
        if (page > 1)
            return new OnlineMidiSearchPage(Array.Empty<OnlineMidiItem>(), page, 1, 0);

        var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}";
        var html = await GetStringAsync(url, $"{BaseUrl}/", cancellationToken);

        var items = new List<OnlineMidiItem>();
        var seenIds = new HashSet<string>();

        foreach (Match match in ResultRegex.Matches(html))
        {
            var id = match.Groups["id"].Value;
            if (!seenIds.Add(id))
                continue;

            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = "Unknown";

            items.Add(new OnlineMidiItem(id, title, match.Groups["ref"].Value, "FreeMidi"));
        }

        return new OnlineMidiSearchPage(items, 1, 1, items.Count);
    }

    public async Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken)
    {
        var pageUrl = $"{BaseUrl}/{item.Reference}";

        // Step 1: visit the song page so FreeMidi sets the session cookie its getter requires.
        var songPageHtml = await GetStringAsync(pageUrl, $"{BaseUrl}/", cancellationToken);

        // Resolve the getter id from the page; fall back to the id embedded in the reference.
        var getterMatch = GetterRegex.Match(songPageHtml);
        var getterId = getterMatch.Success ? getterMatch.Groups["id"].Value : ExtractIdFromReference(item.Reference);
        if (string.IsNullOrEmpty(getterId))
            throw new InvalidOperationException("Could not resolve the FreeMidi download link.");

        // Step 2: fetch the actual MIDI bytes.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/getter-{getterId}");
        ApplyBrowserHeaders(request, pageUrl);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new[] { new DownloadedMidi(item.Name, bytes) };
    }

    private async Task<string> GetStringAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBrowserHeaders(request, referer);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Referrer = new Uri(referer);
    }

    private static string ExtractIdFromReference(string reference)
    {
        var match = Regex.Match(reference, "download3-(\\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
