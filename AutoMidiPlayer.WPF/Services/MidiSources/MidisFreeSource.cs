using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// MIDI source backed by MidisFree (https://midisfree.com), a large library of complete,
/// popular-song MIDI files (~100k). It is browsed A–Z and searched site-wide; downloads use
/// the site's WordPress Download Manager two-step flow (visit the song page to obtain a
/// one-time token, then fetch the file).
/// </summary>
public sealed class MidisFreeSource(HttpClient client) : IMidiSource
{
    private const string BaseUrl = "https://midisfree.com";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // Song links appear as <a href="...download/slug/" rel="bookmark">Title.mid</a> on search
    // pages and <h4 ...><a href='...download/slug/'>Title.mid</a> on the recent list; this matches both.
    private static readonly Regex EntryRegex = new(
        "<a href=[\"'](?<url>https://midisfree\\.com/download/[^\"']+)[\"'][^>]*>(?<title>[^<]*)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // WordPress Download Manager link: ?wpdmdl=12345&refresh=abc123
    private static readonly Regex DownloadTokenRegex = new(
        "wpdmdl=(?<id>\\d+)&(?:amp;)?refresh=(?<token>[a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Presence of a "next page" link indicates more pages are available.
    private static readonly Regex NextPageRegex = new(
        "class=\"next page-numbers\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "MidisFree";

    public async Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        if (page < 1)
            page = 1;

        var browsing = string.IsNullOrWhiteSpace(query);

        // Browse (empty query) shows the site's recent songs (single page); a query searches the whole site.
        if (browsing && page > 1)
            return new OnlineMidiSearchPage(Array.Empty<OnlineMidiItem>(), page, 1, 0);

        string url;
        if (browsing)
            url = $"{BaseUrl}/";
        else
            url = page > 1
                ? $"{BaseUrl}/page/{page}/?s={Uri.EscapeDataString(query)}"
                : $"{BaseUrl}/?s={Uri.EscapeDataString(query)}";

        var html = await GetStringAsync(url, $"{BaseUrl}/", cancellationToken);

        var items = new List<OnlineMidiItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in EntryRegex.Matches(html))
        {
            var detailUrl = match.Groups["url"].Value;

            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();

            // Skip empty anchors (e.g. thumbnail image links that wrap no text).
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (!seen.Add(detailUrl))
                continue;

            if (title.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
                title = title[..^4].Trim();
            else if (title.EndsWith(".midi", StringComparison.OrdinalIgnoreCase))
                title = title[..^5].Trim();

            if (string.IsNullOrWhiteSpace(title))
                title = "Unknown";

            items.Add(new OnlineMidiItem(detailUrl, title, detailUrl, null));
        }

        var pageTotal = browsing
            ? 1
            : (NextPageRegex.IsMatch(html) ? page + 1 : page);

        return new OnlineMidiSearchPage(items, page, pageTotal, items.Count);
    }

    public async Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken)
    {
        var pageUrl = item.Reference;

        // Step 1: visit the song page to get the one-time download token (and set the session cookie).
        var songPageHtml = await GetStringAsync(pageUrl, $"{BaseUrl}/", cancellationToken);

        var tokenMatch = DownloadTokenRegex.Match(songPageHtml);
        if (!tokenMatch.Success)
            throw new InvalidOperationException("Could not resolve the MidisFree download link.");

        var separator = pageUrl.Contains('?') ? "&" : "?";
        var downloadUrl = $"{pageUrl}{separator}wpdmdl={tokenMatch.Groups["id"].Value}&refresh={tokenMatch.Groups["token"].Value}";

        // Step 2: fetch the actual MIDI bytes (cookies from step 1 are sent automatically).
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
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
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Referrer = new Uri(referer);
    }
}
