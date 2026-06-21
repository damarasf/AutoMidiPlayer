using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMidiPlayer.Data;
using AutoMidiPlayer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using StyletIoC;

namespace AutoMidiPlayer.WPF.Services.MidiShow;

/// <summary>
/// Local store for "Discover" (MidiShow) favorites, persisted in the app database. This is
/// independent of MidiShow's own per-account favorites (server-side sync is a later phase).
/// </summary>
public sealed class OnlineFavoritesService
{
    private readonly IContainer _ioc;

    public OnlineFavoritesService(IContainer ioc) => _ioc = ioc;

    /// <summary>Ids of every locally-favorited MidiShow track (for quick "is favorited" checks).</summary>
    public async Task<HashSet<string>> LoadIdsAsync()
    {
        await using var db = _ioc.Get<PlayerContext>();
        var ids = await db.OnlineFavorites.Select(f => f.Id).ToListAsync();
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    /// <summary>All favorites, newest first.</summary>
    public async Task<List<OnlineFavorite>> GetAllAsync()
    {
        await using var db = _ioc.Get<PlayerContext>();
        return await db.OnlineFavorites
            .OrderByDescending(f => f.DateAdded)
            .ToListAsync();
    }

    /// <summary>Adds a favorite (no-op if already present). Returns true if it was added.</summary>
    public async Task<bool> AddAsync(MidiShowItem item)
    {
        if (item is null || string.IsNullOrEmpty(item.Id))
            return false;

        await using var db = _ioc.Get<PlayerContext>();
        if (await db.OnlineFavorites.AnyAsync(f => f.Id == item.Id))
            return false;

        db.OnlineFavorites.Add(new OnlineFavorite
        {
            Id = item.Id,
            PageUrl = item.PageUrl,
            Title = item.Title,
            Uploader = item.Uploader,
            ThumbnailUrl = item.ThumbnailUrl,
            Standard = item.Standard,
            Duration = string.IsNullOrEmpty(item.Duration) ? null : item.Duration,
            Category = string.IsNullOrEmpty(item.Category) ? null : item.Category,
            DateAdded = DateTime.Now
        });

        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Removes a favorite by id (no-op if absent). Returns true if one was removed.</summary>
    public async Task<bool> RemoveAsync(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        await using var db = _ioc.Get<PlayerContext>();
        var existing = await db.OnlineFavorites.FirstOrDefaultAsync(f => f.Id == id);
        if (existing is null)
            return false;

        db.OnlineFavorites.Remove(existing);
        await db.SaveChangesAsync();
        return true;
    }
}
