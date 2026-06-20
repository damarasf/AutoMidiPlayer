using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoMidiPlayer.Data;
using AutoMidiPlayer.WPF.Controls.Snackbar;
using AutoMidiPlayer.WPF.Services;
using Stylet;

namespace AutoMidiPlayer.WPF.ViewModels;

/// <summary>
/// Full-page browser for online MIDI sources. Shows a browseable/searchable list one page at a
/// time, lets the user tick the entries they want, and adds every selected MIDI file to the song
/// library in one action.
/// </summary>
public class BrowseViewModel : Screen
{
    public enum ItemState
    {
        Idle,
        Downloading,
        Added,
        Failed
    }

    /// <summary>View model for a single browseable result row.</summary>
    public sealed class BrowseItemViewModel : PropertyChangedBase
    {
        public BrowseItemViewModel(OnlineMidiItem item) => Item = item;

        public OnlineMidiItem Item { get; }

        public string Name => Item.Name;

        public string? Detail => Item.Detail;

        public bool IsSelected { get; set; }

        public ItemState State { get; set; } = ItemState.Idle;

        public bool IsDownloading => State == ItemState.Downloading;

        public bool ShowStatus => State is ItemState.Added or ItemState.Failed;

        public string StatusText => State switch
        {
            ItemState.Added => "Added",
            ItemState.Failed => "Failed",
            _ => string.Empty
        };

        /// <summary>An already-added row cannot be re-selected.</summary>
        public bool CanSelect => State != ItemState.Added;
    }

    private readonly MainWindowViewModel _main;
    private readonly MidiDownloadService _downloadService = new();

    private DispatcherTimer? _searchDebounceTimer;
    private CancellationTokenSource? _searchCts;
    private int _currentPage;
    private int _pageTotal;
    private bool _ready;

    public BrowseViewModel(MainWindowViewModel main)
    {
        _main = main;

        // Default to the first (most complete) source so a rich list appears immediately.
        SelectedSource = Sources[0];
    }

    // --- Tabs: native multi-source search vs the embedded MidiShow browser ----

    public enum OnlineTab
    {
        Search,
        MidiShow
    }

    public OnlineTab SelectedTab { get; private set; } = OnlineTab.Search;

    public bool IsSearchTab => SelectedTab == OnlineTab.Search;

    public bool IsMidiShowTab => SelectedTab == OnlineTab.MidiShow;

    /// <summary>The embedded MidiShow browser, created lazily on first use.</summary>
    public MidiShowViewModel? MidiShow { get; private set; }

    public void ShowSearch()
    {
        SelectedTab = OnlineTab.Search;
        NotifyTabsChanged();
    }

    public void ShowMidiShow()
    {
        // Spin up the WebView2-backed browser only when the user first opens this tab.
        MidiShow ??= new MidiShowViewModel(_main);
        NotifyOfPropertyChange(nameof(MidiShow));

        SelectedTab = OnlineTab.MidiShow;
        NotifyTabsChanged();
    }

    private void NotifyTabsChanged()
    {
        NotifyOfPropertyChange(nameof(SelectedTab));
        NotifyOfPropertyChange(nameof(IsSearchTab));
        NotifyOfPropertyChange(nameof(IsMidiShowTab));
    }

    public IReadOnlyList<IMidiSource> Sources => _downloadService.Sources;

    public IMidiSource SelectedSource { get; set; }

    public IReadOnlyList<MidiCategory> Categories => SelectedSource.Categories;

    public bool HasCategories => Categories.Count > 0;

    public MidiCategory? SelectedCategory { get; set; }

    public string SearchText { get; set; } = string.Empty;

    public ObservableCollection<BrowseItemViewModel> Results { get; } = new();

    public bool HasResults => Results.Count > 0;

    public bool IsSearching { get; private set; }

    public bool IsAdding { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public bool HasStatusMessage => !IsSearching && !string.IsNullOrEmpty(StatusMessage);

    /// <summary>Hint shown in the search box; differs for browse vs free-text sources.</summary>
    public string SearchPlaceholder => HasCategories
        ? "Search songs by title, or pick a letter to browse..."
        : "Search songs by title (e.g. Coldplay, Bohemian Rhapsody)...";

    public bool CanSearch => !IsSearching;

    // --- Pagination ---------------------------------------------------------

    public int CurrentPage => _currentPage;

    public string PageInfo => $"Page {Math.Max(_currentPage, 1)}";

    public bool CanGoPrevious => _currentPage > 1 && !IsSearching;

    public bool CanGoNext => _currentPage < _pageTotal && !IsSearching;

    /// <summary>Only show the pager when there is more than one page to move through.</summary>
    public bool ShowPagination => HasResults && (_currentPage > 1 || _currentPage < _pageTotal);

    // --- Selection ----------------------------------------------------------

    public int SelectedCount => Results.Count(result => result.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public bool CanAddSelected => HasSelection && !IsAdding;

    public string SelectionSummary => SelectedCount == 0
        ? "No songs selected"
        : $"{SelectedCount} selected";

    public string AddSelectedText => IsAdding
        ? "Adding..."
        : SelectedCount > 0
            ? $"Add {SelectedCount} to Songs"
            : "Add to Songs";

    protected override void OnInitialActivate()
    {
        base.OnInitialActivate();

        _ready = true;

        // Kick off the first browse list.
        if (HasCategories && SelectedCategory is null)
            SelectedCategory = Categories[0];
        else
            _ = LoadPageAsync(1);
    }

    public void OnSelectedSourceChanged()
    {
        NotifyOfPropertyChange(nameof(Categories));
        NotifyOfPropertyChange(nameof(HasCategories));
        NotifyOfPropertyChange(nameof(SearchPlaceholder));

        ResetResults();

        if (HasCategories)
        {
            SelectedCategory = Categories[0];
        }
        else
        {
            SelectedCategory = null;
            if (_ready)
                _ = LoadPageAsync(1);
        }
    }

    public void OnSelectedCategoryChanged()
    {
        if (_ready && SelectedCategory is not null)
            _ = LoadPageAsync(1);
    }

    public void OnSearchTextChanged()
    {
        if (!_ready)
            return;

        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= OnSearchDebounceTick;
        _searchDebounceTimer.Tick += OnSearchDebounceTick;
        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        _ = LoadPageAsync(1);
    }

    public void Search() => _ = LoadPageAsync(1);

    public void NextPage()
    {
        if (CanGoNext)
            _ = LoadPageAsync(_currentPage + 1);
    }

    public void PreviousPage()
    {
        if (CanGoPrevious)
            _ = LoadPageAsync(_currentPage - 1);
    }

    public void SelectAll()
    {
        foreach (var result in Results.Where(result => result.CanSelect))
            result.IsSelected = true;

        RefreshSelectionState();
    }

    public void ClearSelection()
    {
        foreach (var result in Results)
            result.IsSelected = false;

        RefreshSelectionState();
    }

    /// <summary>Loads a single page of results, replacing whatever is currently shown.</summary>
    private async Task LoadPageAsync(int page)
    {
        if (IsSearching)
            return;

        if (page < 1)
            page = 1;

        var query = SearchText?.Trim() ?? string.Empty;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        ClearResultItems();
        SetStatus(string.Empty);
        IsSearching = true;
        RefreshPaginationState();

        try
        {
            var result = await _downloadService.SearchAsync(SelectedSource, query, page, SelectedCategory, token);

            if (token.IsCancellationRequested)
                return;

            _currentPage = result.Page;
            _pageTotal = result.PageTotal;

            foreach (var item in result.Items)
                AddResultItem(item);

            NotifyOfPropertyChange(nameof(HasResults));

            if (Results.Count == 0)
                SetStatus(string.IsNullOrEmpty(query)
                    ? "No MIDI files found here."
                    : $"No MIDI files found for \"{query}\".");
        }
        catch (OperationCanceledException)
        {
            // A newer request superseded this one; ignore.
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            SetStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsSearching = false;

            RefreshPaginationState();
        }
    }

    public async Task AddSelected()
    {
        var selected = Results
            .Where(result => result.IsSelected && result.State != ItemState.Added)
            .ToList();

        if (selected.Count == 0)
            return;

        IsAdding = true;
        RefreshSelectionState();

        var addedFiles = 0;
        var failed = 0;

        foreach (var item in selected)
        {
            item.State = ItemState.Downloading;

            try
            {
                var paths = await _downloadService.DownloadAsync(SelectedSource, item.Item, CancellationToken.None);
                await _main.FileService.AddFiles(paths);

                item.State = ItemState.Added;
                item.IsSelected = false;
                addedFiles += paths.Count;
            }
            catch (Exception ex)
            {
                item.State = ItemState.Failed;
                failed++;
                Logger.LogException(ex);
            }
        }

        IsAdding = false;
        RefreshSelectionState();

        if (addedFiles > 0)
        {
            var fileWord = addedFiles == 1 ? "file" : "files";
            SnackbarService.Success(
                "Added to Songs",
                $"{addedFiles} MIDI {fileWord} added to your songs.");

            SetStatus(failed > 0
                ? $"Added {addedFiles} MIDI {fileWord}; {failed} could not be downloaded."
                : $"Added {addedFiles} MIDI {fileWord} to your songs.");
        }
        else
        {
            SetStatus("Could not add the selected items. Please try again.");
        }
    }

    private void AddResultItem(OnlineMidiItem item)
    {
        var viewModel = new BrowseItemViewModel(item);
        viewModel.PropertyChanged += OnResultItemPropertyChanged;
        Results.Add(viewModel);
    }

    private void OnResultItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowseItemViewModel.IsSelected))
            RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        NotifyOfPropertyChange(nameof(SelectedCount));
        NotifyOfPropertyChange(nameof(HasSelection));
        NotifyOfPropertyChange(nameof(CanAddSelected));
        NotifyOfPropertyChange(nameof(SelectionSummary));
        NotifyOfPropertyChange(nameof(AddSelectedText));
    }

    private void RefreshPaginationState()
    {
        NotifyOfPropertyChange(nameof(CurrentPage));
        NotifyOfPropertyChange(nameof(PageInfo));
        NotifyOfPropertyChange(nameof(CanGoPrevious));
        NotifyOfPropertyChange(nameof(CanGoNext));
        NotifyOfPropertyChange(nameof(ShowPagination));
    }

    /// <summary>Clears just the visible rows (keeps page counters).</summary>
    private void ClearResultItems()
    {
        foreach (var result in Results)
            result.PropertyChanged -= OnResultItemPropertyChanged;

        Results.Clear();
        NotifyOfPropertyChange(nameof(HasResults));
        RefreshSelectionState();
    }

    /// <summary>Clears rows and resets pagination back to the start.</summary>
    private void ResetResults()
    {
        ClearResultItems();
        _currentPage = 0;
        _pageTotal = 0;
        RefreshPaginationState();
        SetStatus(string.Empty);
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        NotifyOfPropertyChange(nameof(StatusMessage));
        NotifyOfPropertyChange(nameof(HasStatusMessage));
    }
}
