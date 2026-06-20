using System;
using System.Collections.Generic;

namespace AutoMidiPlayer.WPF.Services.AudioToMidi;

/// <summary>A decoded note event in seconds.</summary>
internal readonly record struct NoteEvent(double StartSeconds, double EndSeconds, int Pitch, float Amplitude);

/// <summary>
/// C# port of basic_pitch/note_creation.py (the polyphonic note decoder).
/// Pitch bends are intentionally omitted — they are not used by the key player.
/// </summary>
internal static class NoteCreation
{
    private const double MagicAlignmentOffset = 0.0018;

    public static List<NoteEvent> Decode(
        BasicPitchOutput output,
        float onsetThresh,
        float frameThresh,
        int minNoteLen,
        bool inferOnsets = true,
        bool melodiaTrick = true,
        int energyTol = BasicPitchConstants.EnergyTolerance)
    {
        var frames = output.Note;
        var onsets = output.Onset;

        var nFrames = frames.Length;
        if (nFrames == 0)
            return new List<NoteEvent>();

        var nFreq = frames[0].Length;

        if (inferOnsets)
            onsets = GetInferedOnsets(onsets, frames);

        // remaining_energy = copy of frames
        var remaining = new float[nFrames][];
        for (var t = 0; t < nFrames; t++)
            remaining[t] = (float[])frames[t].Clone();

        var times = ModelFramesToTime(nFrames);
        var events = new List<NoteEvent>();

        // Peaks of the onset matrix along the time axis (scipy.signal.argrelmax, order=1).
        // np.where iterates (t asc, f asc); processing goes in reverse.
        var peaks = new List<(int T, int F)>();
        for (var t = 1; t < nFrames - 1; t++)
        {
            var row = onsets[t];
            var prev = onsets[t - 1];
            var next = onsets[t + 1];
            for (var f = 0; f < nFreq; f++)
            {
                if (row[f] > prev[f] && row[f] > next[f] && row[f] >= onsetThresh)
                    peaks.Add((t, f));
            }
        }

        for (var p = peaks.Count - 1; p >= 0; p--)
        {
            var (noteStart, freq) = peaks[p];
            if (noteStart >= nFrames - 1)
                continue;

            // Walk forward until the frame energy stays below threshold for energyTol frames.
            var i = noteStart + 1;
            var k = 0;
            while (i < nFrames - 1 && k < energyTol)
            {
                if (remaining[i][freq] < frameThresh) k++;
                else k = 0;
                i++;
            }

            i -= k;

            if (i - noteStart <= minNoteLen)
                continue;

            ZeroEnergy(remaining, noteStart, i, freq, nFreq);

            events.Add(new NoteEvent(
                times[noteStart], times[i], freq + BasicPitchConstants.MidiOffset,
                Mean(frames, noteStart, i, freq)));
        }

        if (melodiaTrick)
        {
            while (true)
            {
                var (maxVal, iMid, freq) = ArgMax(remaining);
                if (maxVal <= frameThresh)
                    break;

                remaining[iMid][freq] = 0;

                // forward pass
                var i = iMid + 1;
                var k = 0;
                while (i < nFrames - 1 && k < energyTol)
                {
                    if (remaining[i][freq] < frameThresh) k++;
                    else k = 0;

                    remaining[i][freq] = 0;
                    if (freq < nFreq - 1) remaining[i][freq + 1] = 0;
                    if (freq > 0) remaining[i][freq - 1] = 0;
                    i++;
                }

                var iEnd = i - 1 - k;

                // backward pass
                i = iMid - 1;
                k = 0;
                while (i > 0 && k < energyTol)
                {
                    if (remaining[i][freq] < frameThresh) k++;
                    else k = 0;

                    remaining[i][freq] = 0;
                    if (freq < nFreq - 1) remaining[i][freq + 1] = 0;
                    if (freq > 0) remaining[i][freq - 1] = 0;
                    i--;
                }

                var iStart = i + 1 + k;

                if (iEnd - iStart <= minNoteLen)
                    continue;

                events.Add(new NoteEvent(
                    times[iStart], times[iEnd], freq + BasicPitchConstants.MidiOffset,
                    Mean(frames, iStart, iEnd, freq)));
            }
        }

        return events;
    }

    private static void ZeroEnergy(float[][] remaining, int startFrame, int endFrame, int freq, int nFreq)
    {
        for (var t = startFrame; t < endFrame; t++)
        {
            remaining[t][freq] = 0;
            if (freq < nFreq - 1) remaining[t][freq + 1] = 0;
            if (freq > 0) remaining[t][freq - 1] = 0;
        }
    }

    private static float Mean(float[][] frames, int startFrame, int endFrame, int freq)
    {
        if (endFrame <= startFrame)
            return 0f;

        var sum = 0f;
        for (var t = startFrame; t < endFrame; t++)
            sum += frames[t][freq];

        return sum / (endFrame - startFrame);
    }

    private static (float Value, int T, int F) ArgMax(float[][] matrix)
    {
        var best = float.NegativeInfinity;
        int bestT = 0, bestF = 0;
        for (var t = 0; t < matrix.Length; t++)
        {
            var row = matrix[t];
            for (var f = 0; f < row.Length; f++)
            {
                if (row[f] > best)
                {
                    best = row[f];
                    bestT = t;
                    bestF = f;
                }
            }
        }

        return (best, bestT, bestF);
    }

    /// <summary>Infer extra onsets from large positive jumps in frame energy (n_diff = 2).</summary>
    private static float[][] GetInferedOnsets(float[][] onsets, float[][] frames, int nDiff = 2)
    {
        var nFrames = frames.Length;
        var nFreq = frames[0].Length;

        // frame_diff[t,f] = min over n in 1..nDiff of (frames[t,f] - (t>=n ? frames[t-n,f] : 0)), clamped >= 0.
        var frameDiff = new float[nFrames][];
        var maxDiff = 0f;
        for (var t = 0; t < nFrames; t++)
        {
            frameDiff[t] = new float[nFreq];
            for (var f = 0; f < nFreq; f++)
            {
                var minVal = float.PositiveInfinity;
                for (var n = 1; n <= nDiff; n++)
                {
                    var prev = t >= n ? frames[t - n][f] : 0f;
                    var d = frames[t][f] - prev;
                    if (d < minVal) minVal = d;
                }

                if (minVal < 0) minVal = 0;
                frameDiff[t][f] = minVal;
                if (minVal > maxDiff) maxDiff = minVal;
            }
        }

        // Zero the first nDiff rows.
        for (var t = 0; t < nDiff && t < nFrames; t++)
            Array.Clear(frameDiff[t], 0, nFreq);

        var maxOnset = 0f;
        for (var t = 0; t < nFrames; t++)
            for (var f = 0; f < nFreq; f++)
                if (onsets[t][f] > maxOnset) maxOnset = onsets[t][f];

        var result = new float[nFrames][];
        for (var t = 0; t < nFrames; t++)
        {
            result[t] = new float[nFreq];
            for (var f = 0; f < nFreq; f++)
            {
                // Rescale frame_diff to share the onset maximum, then take the elementwise max.
                var rescaled = maxDiff > 0 ? maxOnset * frameDiff[t][f] / maxDiff : 0f;
                result[t][f] = Math.Max(onsets[t][f], rescaled);
            }
        }

        return result;
    }

    /// <summary>
    /// Maps output frame indices to seconds, undoing the per-window offset (model_frames_to_time).
    /// </summary>
    private static double[] ModelFramesToTime(int nFrames)
    {
        const double hopSeconds = (double)BasicPitchConstants.FftHop / BasicPitchConstants.AudioSampleRate;
        var windowOffset = hopSeconds *
            (BasicPitchConstants.AnnotNFrames - (double)BasicPitchConstants.AudioNSamples / BasicPitchConstants.FftHop)
            + MagicAlignmentOffset;

        var times = new double[nFrames];
        for (var i = 0; i < nFrames; i++)
        {
            var original = i * hopSeconds;
            var windowNumber = Math.Floor((double)i / BasicPitchConstants.AnnotNFrames);
            times[i] = original - windowOffset * windowNumber;
        }

        return times;
    }
}
