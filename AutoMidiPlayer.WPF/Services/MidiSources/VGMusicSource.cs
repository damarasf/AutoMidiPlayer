using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// MIDI source backed by VGMusic (https://www.vgmusic.com), a large video-game music archive.
/// VGMusic has no free-text search, so it is browsed by console: a console page is fetched once,
/// parsed into a song list, cached, and then filtered by the user's query.
/// </summary>
public sealed class VGMusicSource(HttpClient client) : IMidiSource
{
    private const int PageSize = 40;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // Matches either a game header row or a song link, in document order.
    private static readonly Regex EntryRegex = new(
        "<tr class=\"gameheader\">\\s*<td[^>]*>(?<game>[^<]*)</td>" +
        "|<a href=\"(?<href>[^\"]+\\.midi?)\">(?<title>[^<]*)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, List<OnlineMidiItem>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public string Name => "VGMusic (game music)";

    public IReadOnlyList<MidiCategory> Categories { get; } = new MidiCategory[]
    {
        new("Nintendo", "https://www.vgmusic.com/music/console/nintendo/nes/"),
        new("Game Boy", "https://www.vgmusic.com/music/console/nintendo/gameboy/"),
        new("Super Nintendo", "https://www.vgmusic.com/music/console/nintendo/snes/"),
        new("Nintendo 64", "https://www.vgmusic.com/music/console/nintendo/n64/"),
        new("VirtualBoy", "https://www.vgmusic.com/music/console/nintendo/virtualboy/"),
        new("Gameboy Advance", "https://www.vgmusic.com/music/console/nintendo/gba/"),
        new("GameCube", "https://www.vgmusic.com/music/console/nintendo/gamecube/"),
        new("Nintendo DS", "https://www.vgmusic.com/music/console/nintendo/ds/"),
        new("Nintendo 3DS", "https://www.vgmusic.com/music/console/nintendo/3ds/"),
        new("Nintendo Wii", "https://www.vgmusic.com/music/console/nintendo/wii/"),
        new("Nintendo Wii U", "https://www.vgmusic.com/music/console/nintendo/wiiu/"),
        new("Nintendo Switch", "https://www.vgmusic.com/music/console/nintendo/switch/"),
        new("Sega Master System", "https://www.vgmusic.com/music/console/sega/master/"),
        new("Sega Game Gear", "https://www.vgmusic.com/music/console/sega/gamegear/"),
        new("Sega Genesis", "https://www.vgmusic.com/music/console/sega/genesis/"),
        new("Sega CD", "https://www.vgmusic.com/music/console/sega/segacd/"),
        new("Sega 32x", "https://www.vgmusic.com/music/console/sega/32x/"),
        new("Sega Saturn", "https://www.vgmusic.com/music/console/sega/saturn/"),
        new("Sega Dreamcast", "https://www.vgmusic.com/music/console/sega/dreamcast/"),
        new("Sony PlayStation", "https://www.vgmusic.com/music/console/sony/ps1/"),
        new("Sony PlayStation 2", "https://www.vgmusic.com/music/console/sony/ps2/"),
        new("Sony PlayStation 3", "https://www.vgmusic.com/music/console/sony/ps3/"),
        new("Sony PlayStation 4", "https://www.vgmusic.com/music/console/sony/ps4/"),
        new("Sony PlayStation Portable", "https://www.vgmusic.com/music/console/sony/psp/"),
        new("Xbox", "https://www.vgmusic.com/music/console/microsoft/xbox/"),
        new("Xbox 360", "https://www.vgmusic.com/music/console/microsoft/xbox360/"),
        new("Xbox One", "https://www.vgmusic.com/music/console/microsoft/xboxone/"),
        new("TurboGrafx-16", "https://www.vgmusic.com/music/console/nec/tg16/"),
        new("Turbo Duo", "https://www.vgmusic.com/music/console/nec/tduo/"),
        new("SuperGrafx", "https://www.vgmusic.com/music/console/nec/sgx/"),
        new("PC-FX", "https://www.vgmusic.com/music/console/nec/pcfx/"),
        new("SNK Neo-Geo", "https://www.vgmusic.com/music/console/snk/neogeo/"),
        new("SNK Neo-Geo Pocket", "https://www.vgmusic.com/music/console/snk/neogeopocket/"),
        new("Atari 2600", "https://www.vgmusic.com/music/console/atari/2600/"),
        new("Atari 7800", "https://www.vgmusic.com/music/console/atari/7800/"),
        new("Atari Lynx", "https://www.vgmusic.com/music/console/atari/lynx/"),
        new("Colecovision", "https://www.vgmusic.com/music/console/coleco/colecovision/"),
        new("Mattel Intellivision", "https://www.vgmusic.com/music/console/mattel/intellivision/"),
        new("Magnavox Odyssey2", "https://www.vgmusic.com/music/console/magnavox/odyssey2/"),
        new("3DO", "https://www.vgmusic.com/music/console/3do/3do/"),
        new("Philips CD-i", "https://www.vgmusic.com/music/console/philips/cd-i/"),
        new("Amiga Computer", "https://www.vgmusic.com/music/computer/commodore/amiga/"),
        new("Amstrad CPC", "https://www.vgmusic.com/music/computer/amstrad/amstradcpc/"),
        new("Apple II", "https://www.vgmusic.com/music/computer/apple/appleii/"),
        new("Apple Macintosh", "https://www.vgmusic.com/music/computer/apple/macintosh/"),
        new("Atari Computers", "https://www.vgmusic.com/music/computer/atari/atari/"),
        new("Commodore 64/128", "https://www.vgmusic.com/music/computer/commodore/commodore/"),
        new("Microsoft DOS/Windows", "https://www.vgmusic.com/music/computer/microsoft/windows/"),
        new("MSX Computer", "https://www.vgmusic.com/music/computer/miscellaneous/msx/"),
        new("NEC PC-88", "https://www.vgmusic.com/music/computer/nec/pc-88/"),
        new("NEC PC-98", "https://www.vgmusic.com/music/computer/nec/pc-98/"),
        new("Sharp X68000", "https://www.vgmusic.com/music/computer/sharp/x68000/"),
        new("Sinclair Spectrum", "https://www.vgmusic.com/music/computer/sinclair/spectrum/"),
        new("Tomy Tutor", "https://www.vgmusic.com/music/computer/tomy/tutor/"),
    };

    public async Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        // VGMusic only works when browsing a console.
        if (category is null)
            return new OnlineMidiSearchPage(Array.Empty<OnlineMidiItem>(), page, 0, 0);

        var allSongs = await GetConsoleSongsAsync(category.Reference, cancellationToken);

        var filtered = string.IsNullOrWhiteSpace(query)
            ? allSongs
            : allSongs.Where(song =>
                song.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (song.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        var pageTotal = (int)Math.Ceiling(filtered.Count / (double)PageSize);
        var pageItems = filtered.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        return new OnlineMidiSearchPage(pageItems, page, pageTotal, filtered.Count);
    }

    public async Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Reference);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new[] { new DownloadedMidi(item.Name, bytes) };
    }

    /// <summary>
    /// Fetches and parses a console page (once), caching the resulting song list.
    /// </summary>
    private async Task<List<OnlineMidiItem>> GetConsoleSongsAsync(string consoleUrl, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(consoleUrl, out var cached))
            return cached;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(consoleUrl, out cached))
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Get, consoleUrl);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var songs = ParseConsolePage(html, consoleUrl);
            _cache[consoleUrl] = songs;
            return songs;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static List<OnlineMidiItem> ParseConsolePage(string html, string consoleUrl)
    {
        var baseUri = new Uri(consoleUrl);
        var songs = new List<OnlineMidiItem>();
        var currentGame = string.Empty;

        foreach (Match match in EntryRegex.Matches(html))
        {
            if (match.Groups["game"].Success)
            {
                currentGame = WebUtility.HtmlDecode(match.Groups["game"].Value).Trim();
                continue;
            }

            var href = match.Groups["href"].Value;
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = href;

            if (!Uri.TryCreate(baseUri, href, out var fileUri))
                continue;

            var displayName = string.IsNullOrEmpty(currentGame) ? title : $"{currentGame} — {title}";
            songs.Add(new OnlineMidiItem(href, displayName, fileUri.ToString(), currentGame));
        }

        return songs;
    }
}
