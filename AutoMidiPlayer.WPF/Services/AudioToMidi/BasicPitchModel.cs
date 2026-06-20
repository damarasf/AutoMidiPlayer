using System;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AutoMidiPlayer.WPF.Services.AudioToMidi;

/// <summary>
/// Raw, unwrapped model output. Each array is indexed [timeFrame][frequencyBin].
/// </summary>
internal sealed record BasicPitchOutput(float[][] Note, float[][] Onset, float[][] Contour);

/// <summary>
/// Runs the Basic Pitch ONNX model: windows the audio, runs inference per window,
/// and unwraps the overlapping windows into single time-frequency matrices.
/// Mirrors basic_pitch/inference.py (window_audio_file, run_inference, unwrap_output).
/// </summary>
internal sealed class BasicPitchModel(string modelPath) : IDisposable
{
    private const string InputName = "serving_default_input_2:0";
    private static readonly string[] OutputNames =
    {
        "StatefulPartitionedCall:1", // note  (frames)
        "StatefulPartitionedCall:2", // onset
        "StatefulPartitionedCall:0", // contour
    };

    private readonly InferenceSession _session = new(modelPath);

    public BasicPitchOutput Run(float[] audio)
    {
        const int overlapLen = BasicPitchConstants.OverlappingFrames * BasicPitchConstants.FftHop; // 7680
        const int hopSize = BasicPitchConstants.AudioNSamples - overlapLen;                        // 36164
        const int prepend = overlapLen / 2;                                                        // 3840
        const int nOverlap = BasicPitchConstants.OverlappingFrames / 2;                            // 15
        const int framesPerWindow = BasicPitchConstants.AnnotNFrames - BasicPitchConstants.OverlappingFrames; // 142

        var originalLength = audio.Length;

        // Prepend overlapLen/2 zeros (basic_pitch get_audio_input).
        var padded = new float[prepend + originalLength];
        Array.Copy(audio, 0, padded, prepend, originalLength);

        var note = new List<float[]>();
        var onset = new List<float[]>();
        var contour = new List<float[]>();

        var inputTensor = new DenseTensor<float>(new[] { 1, BasicPitchConstants.AudioNSamples, 1 });

        for (var start = 0; start < padded.Length; start += hopSize)
        {
            for (var j = 0; j < BasicPitchConstants.AudioNSamples; j++)
            {
                var idx = start + j;
                inputTensor[0, j, 0] = idx < padded.Length ? padded[idx] : 0f;
            }

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(InputName, inputTensor) };
            using var results = _session.Run(inputs, OutputNames);

            var resultList = new List<DisposableNamedOnnxValue>(results);
            AppendUnwrappedWindow(resultList[0].AsTensor<float>(), note, nOverlap);   // note
            AppendUnwrappedWindow(resultList[1].AsTensor<float>(), onset, nOverlap);  // onset
            AppendUnwrappedWindow(resultList[2].AsTensor<float>(), contour, nOverlap); // contour
        }

        // Trim to the number of frames expected for the original (unpadded) audio length.
        var keep = (int)((double)originalLength / hopSize * framesPerWindow);
        keep = Math.Min(keep, note.Count);

        return new BasicPitchOutput(
            note.GetRange(0, keep).ToArray(),
            onset.GetRange(0, keep).ToArray(),
            contour.GetRange(0, keep).ToArray());
    }

    /// <summary>
    /// Drops the first and last <paramref name="nOverlap"/> time frames of a window's output
    /// and appends the remaining rows (each a frequency vector) to <paramref name="target"/>.
    /// </summary>
    private static void AppendUnwrappedWindow(Tensor<float> tensor, List<float[]> target, int nOverlap)
    {
        var nFrames = tensor.Dimensions[1];
        var nBins = tensor.Dimensions[2];

        for (var t = nOverlap; t < nFrames - nOverlap; t++)
        {
            var row = new float[nBins];
            for (var f = 0; f < nBins; f++)
                row[f] = tensor[0, t, f];

            target.Add(row);
        }
    }

    public void Dispose() => _session.Dispose();
}
