using System;

namespace AutoMidiPlayer.Data.Entities;

/// <summary>
/// A MidiShow ("Discover") track the user favorited locally. Stores just enough to render
/// the favorite and re-open/download it later, independent of MidiShow's own account favorites.
/// </summary>
public class OnlineFavorite
{
    /// <summary>MidiShow numeric id (primary key).</summary>
    public string Id { get; set; } = null!;

    public string PageUrl { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Uploader { get; set; }

    public string? ThumbnailUrl { get; set; }

    public string? Standard { get; set; }

    public string? Duration { get; set; }

    public string? Category { get; set; }

    public DateTime DateAdded { get; set; }
}
