using System;
using System.IO;
using System.Threading.Tasks;
using AutoMidiPlayer.Data;
using AutoMidiPlayer.WPF.Controls.Snackbar;
using Stylet;

namespace AutoMidiPlayer.WPF.ViewModels;

/// <summary>
/// Embeds the real MidiShow website (https://www.midishow.com/en) in a WebView2 browser.
/// The user logs in and browses/searches MidiShow exactly as on the site; any MIDI they
/// download is captured and imported straight into the song library.
///
/// This avoids scraping MidiShow (which blocks automated/non-browser access) and respects the
/// site's login requirement — every user uses their own MidiShow account in a genuine browser.
/// </summary>
public class MidiShowViewModel : Screen
{
    private readonly MainWindowViewModel _main;

    public MidiShowViewModel(MainWindowViewModel main)
    {
        _main = main;
        DisplayName = "MidiShow";
    }

    /// <summary>The MidiShow page the embedded browser opens on (English MIDI listing).</summary>
    public string StartUrl => "https://www.midishow.com/en/midi";

    /// <summary>Persistent WebView2 profile folder so the MidiShow login survives restarts.</summary>
    public string UserDataFolder => Path.Combine(AppPaths.AppDataDirectory, "webview2", "midishow");

    public string StatusMessage { get; set; } =
        "Log in to MidiShow, then download a MIDI — it is added to your Songs automatically.";

    /// <summary>Imports a MIDI file captured from a MidiShow download into the song library.</summary>
    public async Task ImportDownloadedFileAsync(string path)
    {
        try
        {
            await _main.FileService.AddFiles(new[] { path });

            StatusMessage = $"Added \"{Path.GetFileName(path)}\" to your Songs.";
            SnackbarService.Success("Added from MidiShow", $"{Path.GetFileName(path)} added to your Songs.");
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            StatusMessage = $"Could not import the download: {ex.Message}";
            SnackbarService.Danger("MidiShow import failed", ex.Message);
        }
    }

    public void SetStatus(string message) => StatusMessage = message;
}
