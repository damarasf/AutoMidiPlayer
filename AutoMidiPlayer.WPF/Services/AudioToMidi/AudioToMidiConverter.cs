using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMidiPlayer.Data;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AutoMidiPlayer.WPF.Services.AudioToMidi;

/// <summary>
/// Converts a local audio file to a MIDI file using the Spotify Basic Pitch ONNX model.
/// The ~225 KB model is downloaded to the cache on first use; everything else runs locally.
/// </summary>
public sealed class AudioToMidiConverter
{
    private const string ModelFileName = "nmp.onnx";
    private const string ModelUrl =
        "https://raw.githubusercontent.com/spotify/basic-pitch/main/basic_pitch/saved_models/icassp_2022/nmp.onnx";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoMidiPlayer");
        return client;
    }

    /// <summary>
    /// Converts the given audio file to a MIDI file and returns its path.
    /// </summary>
    public async Task<string> ConvertAsync(string audioPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("Audio file not found.", audioPath);

        progress?.Report("Preparing converter…");
        var modelPath = await EnsureModelAsync(cancellationToken);

        progress?.Report("Decoding audio…");
        var audio = await Task.Run(() => AudioDecoder.DecodeToMono22050(audioPath), cancellationToken);
        if (audio.Length == 0)
            throw new InvalidOperationException("The audio file appears to be empty or could not be decoded.");

        progress?.Report("Transcribing audio to notes (this can take a while)…");
        var events = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var model = new BasicPitchModel(modelPath);
            var output = model.Run(audio);

            var minNoteLen = (int)Math.Round(
                BasicPitchConstants.DefaultMinimumNoteLengthMs / 1000.0
                * ((double)BasicPitchConstants.AudioSampleRate / BasicPitchConstants.FftHop));

            return NoteCreation.Decode(
                output,
                BasicPitchConstants.DefaultOnsetThreshold,
                BasicPitchConstants.DefaultFrameThreshold,
                minNoteLen);
        }, cancellationToken);

        if (events.Count == 0)
            throw new InvalidOperationException("No notes were detected in the audio.");

        progress?.Report("Writing MIDI file…");
        return WriteMidi(audioPath, events);
    }

    private static async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var directory = AppPaths.EnsureConvertCacheDirectory();
        var modelPath = Path.Combine(directory, ModelFileName);

        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 0)
            return modelPath;

        var bytes = await Http.GetByteArrayAsync(ModelUrl, cancellationToken);

        // Write to a temp file then move, so a cancelled/failed download never leaves a partial model.
        var tempPath = modelPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        File.Move(tempPath, modelPath, overwrite: true);

        return modelPath;
    }

    private static string WriteMidi(string audioPath, List<NoteEvent> events)
    {
        var tempoMap = TempoMap.Create(Tempo.FromBeatsPerMinute(120));
        var notes = new List<Note>(events.Count);

        foreach (var note in events)
        {
            if (note.Pitch is < 0 or > 127)
                continue;

            var startTicks = TimeConverter.ConvertFrom(
                new MetricTimeSpan(Math.Max(0, (long)(note.StartSeconds * 1_000_000))), tempoMap);
            var endTicks = TimeConverter.ConvertFrom(
                new MetricTimeSpan(Math.Max(0, (long)(note.EndSeconds * 1_000_000))), tempoMap);
            var length = Math.Max(1, endTicks - startTicks);

            var velocity = Math.Clamp((int)Math.Round(BasicPitchConstants.MidiVelocityScale * note.Amplitude), 1, 127);

            notes.Add(new Note((SevenBitNumber)(byte)note.Pitch)
            {
                Time = startTicks,
                Length = length,
                Velocity = (SevenBitNumber)(byte)velocity,
            });
        }

        var trackChunk = new TrackChunk();
        trackChunk.AddObjects(notes);

        var midiFile = new MidiFile(trackChunk);
        midiFile.ReplaceTempoMap(tempoMap);

        var directory = AppPaths.EnsureDownloadedMidiDirectory();
        var path = GetUniquePath(directory, Path.GetFileNameWithoutExtension(audioPath));
        midiFile.Write(path, overwriteFile: true);

        return path;
    }

    private static string GetUniquePath(string directory, string baseName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        baseName = baseName.Trim();
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "converted";

        if (baseName.Length > 120)
            baseName = baseName[..120];

        var path = Path.Combine(directory, baseName + ".mid");
        var counter = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName} ({counter}).mid");
            counter++;
        }

        return path;
    }
}
