using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoMidiPlayer.WPF.Services;

namespace AutoMidiPlayer.WPF.Dialogs;

/// <summary>
/// Dialog content that searches online MIDI sources and lets the user
/// download results straight into the song library.
/// </summary>
public partial class OnlineMidiView : UserControl, INotifyPropertyChanged
{
    public enum ItemState
    {
        Idle,
        Downloading,
        Added,
        Failed
    }

    /// <summary>
    /// View model for a single online search result row.
    /// </summary>
    public sealed class OnlineMidiItemViewModel : INotifyPropertyChanged
    {
        public OnlineMidiItemViewModel(OnlineMidiItem item) => Item = item;

        public OnlineMidiItem Item { get; }

        public string Name => Item.Name;

        public string? Detail => Item.Detail;

        private ItemState _state = ItemState.Idle;

        public ItemState State
        {
            get => _state;
            set
            {
                if (_state == value)
                    return;

                _state = value;
                NotifyOfPropertyChange(nameof(State));
                NotifyOfPropertyChange(nameof(CanAdd));
                NotifyOfPropertyChange(nameof(IsDownloading));
                NotifyOfPropertyChange(nameof(ShowActionButton));
                NotifyOfPropertyChange(nameof(ActionText));
            }
        }

        public bool CanAdd => State is ItemState.Idle or ItemState.Failed;

        public bool IsDownloading => State == ItemState.Downloading;

        public bool ShowActionButton => State != ItemState.Downloading;

        public string ActionText => State switch
        {
            ItemState.Added => "Added",
            ItemState.Failed => "Retry",
            _ => "Add"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyOfPropertyChange([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly MidiDownloadService _downloadService;
    private readonly Func<IReadOnlyList<string>, Task> _addDownloadedFilesAsync;

    private CancellationTokenSource? _searchCts;
    private int _currentPage;
    private int _pageTotal;
    private bool _isSearching;
    private IMidiSource _selectedSource;
    private MidiCategory? _selectedCategory;

    public OnlineMidiView(MidiDownloadService downloadService, Func<IReadOnlyList<string>, Task> addDownloadedFilesAsync)
    {
        _downloadService = downloadService;
        _addDownloadedFilesAsync = addDownloadedFilesAsync;
        _selectedSource = downloadService.Sources[0];

        InitializeComponent();

        DataContext = this;
    }

    public IReadOnlyList<IMidiSource> Sources => _downloadService.Sources;

    public IMidiSource SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (ReferenceEquals(_selectedSource, value) || value is null)
                return;

            _selectedSource = value;
            NotifyOfPropertyChange(nameof(SelectedSource));
            NotifyOfPropertyChange(nameof(Categories));
            NotifyOfPropertyChange(nameof(HasCategories));

            ClearResults();

            // Browse-based sources auto-load their first category; free-text sources wait for a query.
            if (HasCategories)
                SelectedCategory = Categories[0];
            else
                _selectedCategory = null;
        }
    }

    public IReadOnlyList<MidiCategory> Categories => SelectedSource.Categories;

    public bool HasCategories => Categories.Count > 0;

    public MidiCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Equals(_selectedCategory, value))
                return;

            _selectedCategory = value;
            NotifyOfPropertyChange(nameof(SelectedCategory));

            // Picking a console immediately browses its songs (the search box then filters them).
            if (value is not null)
                _ = RunSearchAsync(resetResults: true);
        }
    }

    public ObservableCollection<OnlineMidiItemViewModel> Results { get; } = new();

    public string SearchText { get; set; } = string.Empty;

    public string StatusMessage { get; private set; } = string.Empty;

    public bool HasStatusMessage => !IsSearching && !string.IsNullOrEmpty(StatusMessage);

    public bool HasResults => Results.Count > 0;

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            _isSearching = value;
            NotifyOfPropertyChange(nameof(IsSearching));
            NotifyOfPropertyChange(nameof(HasStatusMessage));
            NotifyOfPropertyChange(nameof(CanSearch));
            NotifyOfPropertyChange(nameof(CanLoadMore));
        }
    }

    public bool CanSearch => !IsSearching;

    public bool HasMorePages => _currentPage < _pageTotal;

    public bool CanLoadMore => HasMorePages && !IsSearching;

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = RunSearchAsync(resetResults: true);
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) =>
        _ = RunSearchAsync(resetResults: true);

    private void OnLoadMoreClick(object sender, RoutedEventArgs e) =>
        _ = RunSearchAsync(resetResults: false);

    private async Task RunSearchAsync(bool resetResults)
    {
        if (IsSearching)
            return;

        var query = SearchText?.Trim() ?? string.Empty;

        // Free-text sources need a query; browse sources (with a console selected) can list everything.
        if (string.IsNullOrEmpty(query) && !HasCategories)
        {
            SetStatus("Type something to search for.");
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        if (resetResults)
        {
            Results.Clear();
            NotifyOfPropertyChange(nameof(HasResults));
            _currentPage = 0;
            _pageTotal = 0;
        }

        SetStatus(string.Empty);
        IsSearching = true;

        try
        {
            var page = await _downloadService.SearchAsync(SelectedSource, query, _currentPage + 1, SelectedCategory, token);

            if (token.IsCancellationRequested)
                return;

            _currentPage = page.Page;
            _pageTotal = page.PageTotal;

            foreach (var item in page.Items)
                Results.Add(new OnlineMidiItemViewModel(item));

            NotifyOfPropertyChange(nameof(HasResults));
            NotifyOfPropertyChange(nameof(HasMorePages));
            NotifyOfPropertyChange(nameof(CanLoadMore));

            if (Results.Count == 0)
                SetStatus(string.IsNullOrEmpty(query)
                    ? "No MIDI files found here."
                    : $"No MIDI files found for \"{query}\".");
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one; ignore.
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsSearching = false;
        }
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OnlineMidiItemViewModel itemViewModel })
            return;

        if (!itemViewModel.CanAdd)
            return;

        itemViewModel.State = ItemState.Downloading;

        try
        {
            var paths = await _downloadService.DownloadAsync(SelectedSource, itemViewModel.Item, CancellationToken.None);
            await _addDownloadedFilesAsync(paths);
            itemViewModel.State = ItemState.Added;

            if (paths.Count > 1)
                SetStatus($"Added {paths.Count} MIDI files from \"{itemViewModel.Name}\".");
        }
        catch (Exception ex)
        {
            itemViewModel.State = ItemState.Failed;
            SetStatus($"Could not add \"{itemViewModel.Name}\": {ex.Message}");
        }
    }

    private void ClearResults()
    {
        Results.Clear();
        NotifyOfPropertyChange(nameof(HasResults));
        _currentPage = 0;
        _pageTotal = 0;
        NotifyOfPropertyChange(nameof(HasMorePages));
        NotifyOfPropertyChange(nameof(CanLoadMore));
        SetStatus(string.Empty);
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        NotifyOfPropertyChange(nameof(StatusMessage));
        NotifyOfPropertyChange(nameof(HasStatusMessage));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyOfPropertyChange([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
