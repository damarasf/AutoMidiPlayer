using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMidiPlayer.Data;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// Orchestrates searching and downloading MIDI files from one of several online sources.
/// Validates downloaded content and saves it into the local downloads directory.
/// </summary>
public class MidiDownloadService
{
    private static readonly HttpClient Client = CreateClient();

    public MidiDownloadService()
    {
        // Ordered by catalog completeness / popularity (the first is the default).
        Sources = new IMidiSource[]
        {
            new BitMidiSource(Client),   // ~113k popular songs (public API)
            new MidisFreeSource(Client), // ~100k popular songs (scraped)
            new FreeMidiSource(Client)   // pop-rock catalog (scraped)
        };
    }

    /// <summary>The online sources the user can choose between.</summary>
    public IReadOnlyList<IMidiSource> Sources { get; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoMidiPlayer");
        return client;
    }

    /// <summary>
    /// Searches the given source for the query.
    /// </summary>
    public Task<OnlineMidiSearchPage> SearchAsync(IMidiSource source, string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        // An empty query browses the source's catalog; a non-empty one searches/filters within it.
        return source.SearchAsync(query?.Trim() ?? string.Empty, page, category, cancellationToken);
    }

    /// <summary>
    /// Downloads a search result, validates it, and saves the valid MIDI file(s) to disk.
    /// Returns the saved file paths. Throws <see cref="InvalidDataException"/> if nothing valid was found.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadAsync(IMidiSource source, OnlineMidiItem item, CancellationToken cancellationToken)
    {
        var files = await source.DownloadAsync(item, cancellationToken);

        var directory = AppPaths.EnsureDownloadedMidiDirectory();
        var savedPaths = new List<string>();

        foreach (var file in files)
        {
            if (!IsValidMidi(file.Content))
                continue;

            var path = GetUniquePath(directory, file.SuggestedName);
            await File.WriteAllBytesAsync(path, file.Content, cancellationToken);
            savedPaths.Add(path);
        }

        if (savedPaths.Count == 0)
            throw new InvalidDataException("No valid MIDI file was found for this result.");

        return savedPaths;
    }

    /// <summary>
    /// A valid Standard MIDI File begins with the "MThd" header chunk.
    /// </summary>
    private static bool IsValidMidi(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 'M' && bytes[1] == 'T' && bytes[2] == 'h' && bytes[3] == 'd';

    /// <summary>
    /// Builds a safe, collision-free file path inside the downloads directory.
    /// </summary>
    private static string GetUniquePath(string directory, string suggestedName)
    {
        var name = suggestedName;
        if (name.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        else if (name.EndsWith(".midi", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "midi";

        if (name.Length > 120)
            name = name[..120];

        var path = Path.Combine(directory, name + ".mid");
        var counter = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{name} ({counter}).mid");
            counter++;
        }

        return path;
    }
}
