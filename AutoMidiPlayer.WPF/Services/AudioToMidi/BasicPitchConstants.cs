namespace AutoMidiPlayer.WPF.Services.AudioToMidi;

/// <summary>
/// Constants for the Spotify Basic Pitch model, mirroring basic_pitch/constants.py.
/// </summary>
internal static class BasicPitchConstants
{
    public const int FftHop = 256;
    public const int AudioSampleRate = 22050;
    public const int AudioWindowLengthSeconds = 2;

    /// <summary>Frames per second of the model's time-frequency output (22050 / 256 = 86).</summary>
    public const int AnnotationsFps = AudioSampleRate / FftHop;

    /// <summary>Number of output time frames per window (86 * 2 = 172).</summary>
    public const int AnnotNFrames = AnnotationsFps * AudioWindowLengthSeconds;

    /// <summary>Number of audio samples per model input window (22050 * 2 - 256 = 43844).</summary>
    public const int AudioNSamples = AudioSampleRate * AudioWindowLengthSeconds - FftHop;

    /// <summary>Number of piano-key frequency bins for note/onset outputs.</summary>
    public const int NFreqBinsNotes = 88;

    /// <summary>Bins per semitone in the contour output.</summary>
    public const int ContoursBinsPerSemitone = 3;

    /// <summary>Number of contour frequency bins (88 * 3 = 264).</summary>
    public const int NFreqBinsContours = NFreqBinsNotes * ContoursBinsPerSemitone;

    /// <summary>MIDI note number of the lowest output bin (A0).</summary>
    public const int MidiOffset = 21;

    public const int OverlappingFrames = 30;

    // Note-creation defaults (basic_pitch/inference.py).
    public const float DefaultOnsetThreshold = 0.5f;
    public const float DefaultFrameThreshold = 0.3f;
    public const float DefaultMinimumNoteLengthMs = 127.7f;
    public const int EnergyTolerance = 11;
    public const int MidiVelocityScale = 127;
}
