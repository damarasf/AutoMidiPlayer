using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoMidiPlayer.Data;
using AutoMidiPlayer.WPF.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace AutoMidiPlayer.WPF.Views;

public partial class MidiShowView : UserControl
{
    private bool _initialized;

    public MidiShowView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MidiShowViewModel? Vm => DataContext as MidiShowViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;

        var vm = Vm;
        if (vm is null)
            return;

        try
        {
            Directory.CreateDirectory(vm.UserDataFolder);

            // A persistent user-data folder keeps the MidiShow login across app restarts.
            var environment = await CoreWebView2Environment.CreateAsync(null, vm.UserDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.DownloadStarting += OnDownloadStarting;
            // MidiShow opens songs/downloads in new tabs (target=_blank); keep them in this
            // same embedded browser so navigations & downloads are captured instead of escaping
            // to an external browser window.
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Browser.CoreWebView2.Navigate(vm.StartUrl);
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            vm.SetStatus($"Could not start the embedded browser: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps "open in new tab/window" links (MidiShow uses target=_blank) inside this same
    /// embedded browser. This is what lets a download started from such a link reach
    /// <see cref="OnDownloadStarting"/> instead of opening an external browser window.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        var uri = e.Uri;
        if (!string.IsNullOrEmpty(uri) && !uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            Browser.CoreWebView2?.Navigate(uri);
    }

    /// <summary>
    /// Redirects MidiShow MIDI downloads into the app's downloads folder and imports them on completion.
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var suggestedName = Path.GetFileName(e.ResultFilePath) ?? string.Empty;
            var uri = e.DownloadOperation.Uri ?? string.Empty;

            var isMidiName = suggestedName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase)
                             || suggestedName.EndsWith(".midi", StringComparison.OrdinalIgnoreCase);
            var fromMidiShow = uri.Contains("midishow", StringComparison.OrdinalIgnoreCase);

            // Capture MIDI downloads and anything coming from MidiShow; non-MIDI files are
            // filtered out after completion by validating the file header.
            if (!isMidiName && !fromMidiShow)
                return;

            var directory = AppPaths.EnsureDownloadedMidiDirectory();
            var destination = GetUniquePath(directory, suggestedName);
            e.ResultFilePath = destination;

            var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) =>
            {
                if (operation.State != CoreWebView2DownloadState.Completed)
                    return;

                Dispatcher.InvokeAsync(async () =>
                {
                    if (Vm is null)
                        return;

                    // Only import genuine Standard MIDI Files (header "MThd"); drop anything else.
                    if (IsValidMidiFile(destination))
                    {
                        await Vm.ImportDownloadedFileAsync(destination);
                    }
                    else
                    {
                        TryDelete(destination);
                        Vm.SetStatus("That download was not a MIDI file, so it was not added.");
                    }
                });
            };

            Vm?.SetStatus($"Downloading \"{Path.GetFileName(destination)}\"...");
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
        }
    }

    /// <summary>A valid Standard MIDI File begins with the "MThd" header chunk.</summary>
    private static bool IsValidMidiFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) == 4
                   && header[0] == (byte)'M' && header[1] == (byte)'T'
                   && header[2] == (byte)'h' && header[3] == (byte)'d';
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>Builds a safe, collision-free .mid path inside the downloads directory.</summary>
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
            name = "midishow";

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

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is { CanGoBack: true })
            Browser.CoreWebView2.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is { CanGoForward: true })
            Browser.CoreWebView2.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void OnHome(object sender, RoutedEventArgs e) =>
        Browser.CoreWebView2?.Navigate(Vm?.StartUrl ?? "https://www.midishow.com/en/midi");
}
