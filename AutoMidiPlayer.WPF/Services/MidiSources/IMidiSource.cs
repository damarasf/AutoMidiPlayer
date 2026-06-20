using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// A single MIDI entry returned from an online source.
/// </summary>
/// <param name="Id">Stable, source-specific identifier (used for display only).</param>
/// <param name="Name">Human-readable title.</param>
/// <param name="Reference">Opaque, source-specific reference used to download the entry.</param>
/// <param name="Detail">Optional secondary line shown under the name (e.g. play counts).</param>
/// <param name="Origin">The source this item came from. Set by aggregating sources to route downloads.</param>
public sealed record OnlineMidiItem(string Id, string Name, string Reference, string? Detail, IMidiSource? Origin = null);

/// <summary>
/// A page of online MIDI search results.
/// </summary>
public sealed record OnlineMidiSearchPage(IReadOnlyList<OnlineMidiItem> Items, int Page, int PageTotal, int Total)
{
    public bool HasMorePages => Page < PageTotal;
}

/// <summary>
/// A downloaded MIDI file's content together with a suggested file name.
/// A single search result may resolve to more than one MIDI file (e.g. an album/pack).
/// </summary>
public sealed record DownloadedMidi(string SuggestedName, byte[] Content);

/// <summary>
/// A browse category (e.g. a game console) for sources that organize content by category
/// instead of offering free-text search across everything.
/// </summary>
/// <param name="Name">Display name shown in the category picker.</param>
/// <param name="Reference">Opaque, source-specific reference for the category (e.g. a listing URL).</param>
public sealed record MidiCategory(string Name, string Reference);

/// <summary>
/// An online provider that can be searched for MIDI files and downloaded from.
/// </summary>
public interface IMidiSource
{
    /// <summary>Display name shown in the source picker.</summary>
    string Name { get; }

    /// <summary>
    /// Browse categories for sources that require one (e.g. game consoles).
    /// Empty for sources that support free-text search across their whole library.
    /// </summary>
    IReadOnlyList<MidiCategory> Categories => Array.Empty<MidiCategory>();

    /// <summary>
    /// Searches the source for the given query (1-based page).
    /// For category-based sources, <paramref name="category"/> scopes the search and the
    /// query filters within it; an empty query lists everything in the category.
    /// </summary>
    Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken);

    /// <summary>Resolves and downloads the MIDI file(s) for a search result.</summary>
    Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken);
}
