using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using AutoMidiPlayer.WPF.Services.AudioToMidi;

namespace AutoMidiPlayer.WPF.Dialogs;

/// <summary>
/// Dialog content that converts one or more local audio files to MIDI and imports the results.
/// </summary>
public partial class AudioConvertView : UserControl, INotifyPropertyChanged
{
    public enum ItemState
    {
        Pending,
        Converting,
        Done,
        Failed,
    }

    public sealed class AudioConvertItemViewModel(string audioPath) : INotifyPropertyChanged
    {
        public string AudioPath { get; } = audioPath;

        public string FileName { get; } = Path.GetFileName(audioPath);

        private ItemState _state = ItemState.Pending;

        public ItemState State
        {
            get => _state;
            set
            {
                _state = value;
                NotifyOfPropertyChange(nameof(State));
                NotifyOfPropertyChange(nameof(IsConverting));
                NotifyOfPropertyChange(nameof(IsDone));
                NotifyOfPropertyChange(nameof(IsFailed));
            }
        }

        private string _statusText = "Waiting…";

        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                NotifyOfPropertyChange(nameof(StatusText));
            }
        }

        public bool IsConverting => State == ItemState.Converting;

        public bool IsDone => State == ItemState.Done;

        public bool IsFailed => State == ItemState.Failed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyOfPropertyChange([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly AudioToMidiConverter _converter;
    private readonly Func<string, Task> _importAsync;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public AudioConvertView(IReadOnlyList<string> audioPaths, AudioToMidiConverter converter, Func<string, Task> importAsync)
    {
        _converter = converter;
        _importAsync = importAsync;

        InitializeComponent();
        DataContext = this;

        foreach (var path in audioPaths)
            Items.Add(new AudioConvertItemViewModel(path));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ObservableCollection<AudioConvertItemViewModel> Items { get; } = new();

    public string Summary { get; private set; } = string.Empty;

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_started)
            return;

        _started = true;
        _ = ProcessAllAsync(_cts.Token);
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => _cts.Cancel();

    private async Task ProcessAllAsync(CancellationToken cancellationToken)
    {
        var succeeded = 0;
        var failed = 0;

        foreach (var item in Items)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            item.State = ItemState.Converting;
            var progress = new Progress<string>(message => item.StatusText = message);

            try
            {
                var midiPath = await _converter.ConvertAsync(item.AudioPath, progress, cancellationToken);
                await _importAsync(midiPath);

                item.StatusText = "Added to library.";
                item.State = ItemState.Done;
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                item.StatusText = "Cancelled.";
                item.State = ItemState.Failed;
                break;
            }
            catch (Exception ex)
            {
                item.StatusText = ex.Message;
                item.State = ItemState.Failed;
                failed++;
            }
        }

        SetSummary(succeeded, failed);
    }

    private void SetSummary(int succeeded, int failed)
    {
        var parts = new List<string>();
        if (succeeded > 0)
            parts.Add($"{succeeded} added");
        if (failed > 0)
            parts.Add($"{failed} failed");

        Summary = parts.Count > 0 ? string.Join(", ", parts) + "." : string.Empty;
        NotifyOfPropertyChange(nameof(Summary));
        NotifyOfPropertyChange(nameof(HasSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyOfPropertyChange([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
