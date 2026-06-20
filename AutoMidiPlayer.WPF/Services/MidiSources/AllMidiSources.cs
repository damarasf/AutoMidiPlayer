using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoMidiPlayer.WPF.Services;

/// <summary>
/// Aggregate source that searches every underlying source in parallel and
/// interleaves their results, so a single search returns the widest set of MIDI files.
/// Downloads are routed back to the source each result originally came from.
/// </summary>
public sealed class AllMidiSources(IReadOnlyList<IMidiSource> sources) : IMidiSource
{
    public string Name => "All sources";

    public async Task<OnlineMidiSearchPage> SearchAsync(string query, int page, MidiCategory? category, CancellationToken cancellationToken)
    {
        // Only include free-text-search sources; browse-only sources (with categories) need a
        // category selection that "All sources" cannot provide.
        var searchable = sources.Where(source => source.Categories.Count == 0).ToList();

        var pages = await Task.WhenAll(searchable.Select(source => SafeSearchAsync(source, query, page, cancellationToken)));

        var perSource = new List<List<OnlineMidiItem>>();
        var total = 0;
        var hasMore = false;

        for (var i = 0; i < searchable.Count; i++)
        {
            var result = pages[i];
            if (result is null)
                continue;

            total += result.Total;
            if (result.HasMorePages)
                hasMore = true;

            // Tag each item with its origin source so DownloadAsync can route it.
            var origin = searchable[i];
            perSource.Add(result.Items.Select(item => item with { Origin = origin }).ToList());
        }

        var interleaved = Interleave(perSource);
        var pageTotal = hasMore ? page + 1 : page;

        return new OnlineMidiSearchPage(interleaved, page, pageTotal, total);
    }

    public Task<IReadOnlyList<DownloadedMidi>> DownloadAsync(OnlineMidiItem item, CancellationToken cancellationToken)
    {
        var origin = item.Origin ?? sources[0];
        return origin.DownloadAsync(item, cancellationToken);
    }

    private static async Task<OnlineMidiSearchPage?> SafeSearchAsync(
        IMidiSource source, string query, int page, CancellationToken cancellationToken)
    {
        try
        {
            return await source.SearchAsync(query, page, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A single failing source should not break the combined search.
            return null;
        }
    }

    /// <summary>
    /// Round-robins across the per-source result lists so no single source dominates the top.
    /// </summary>
    private static List<OnlineMidiItem> Interleave(List<List<OnlineMidiItem>> lists)
    {
        var result = new List<OnlineMidiItem>();
        if (lists.Count == 0)
            return result;

        var maxLength = lists.Max(list => list.Count);
        for (var index = 0; index < maxLength; index++)
        {
            foreach (var list in lists)
            {
                if (index < list.Count)
                    result.Add(list[index]);
            }
        }

        return result;
    }
}
