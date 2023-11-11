// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Utils;
using osu.Framework.Extensions;
using NAudio.Dsp;
using System.Collections.Generic;

namespace osu.Framework.Audio.Track
{
    /// <summary>
    /// Processes audio sample data such that it can then be consumed to generate waveform plots of the audio.
    /// </summary>
    public class Waveform : IDisposable
    {
        /// <summary>
        /// <see cref="Point"/>s are initially generated to a 1ms resolution to cover most use cases.
        /// </summary>
        private const float resolution = 0.001f;

        /// <summary>
        /// FFT1024 gives ~40hz accuracy.
        /// </summary>
        private const int fft_samples = 1024;

        /// <summary>
        /// Number of bins generated by the FFT. Must correspond to <see cref="fft_samples"/>.
        /// </summary>
        private const int fft_bins = 512;

        /// <summary>
        /// Minimum frequency for low-range (bass) frequencies. Based on lower range of bass drum fallout.
        /// </summary>
        private const float low_min = 20;

        /// <summary>
        /// Minimum frequency for mid-range frequencies. Based on higher range of bass drum fallout.
        /// </summary>
        private const float mid_min = 100;

        /// <summary>
        /// Minimum frequency for high-range (treble) frequencies.
        /// </summary>
        private const float high_min = 2000;

        /// <summary>
        /// Maximum frequency for high-range (treble) frequencies. A sane value.
        /// </summary>
        private const float high_max = 12000;

        private int channels;
        private Point[] points = Array.Empty<Point>();

        private readonly CancellationTokenSource cancelSource = new CancellationTokenSource();

        private readonly Task readTask;

        /// <summary>
        /// Constructs a new <see cref="Waveform"/> from provided audio data.
        /// </summary>
        /// <param name="data">The sample data stream. If null, an empty waveform is constructed.</param>
        public Waveform(Stream? data)
        {
            readTask = Task.Run(() =>
            {
                if (data == null)
                    return;

                const int bytes_per_sample = 4;
                const int sample_rate = 44100;

                // Code below assumes stereo
                channels = 2;

                // GetAudioDecoder returns BASS decoder if any BASS device (including No Sound) is available
                AudioDecoder decoder = SDL2AudioManager.GetAudioDecoder();

                // AudioDecoder will resample data into specified sample rate and channels (44100hz 2ch float)
                AudioDecoder.AudioDecoderData decoderData = decoder.CreateDecoderData(sample_rate, channels, true, SDL2.SDL.AUDIO_F32, data, false);

                Complex[]? complexBuffer = null;

                try
                {
                    // Each "point" is generated from a number of samples, each sample contains a number of channels
                    int samplesPerPoint = (int)(sample_rate * resolution * channels);

                    // Use List as entire length may be inaccurate
                    List<Point> pointList = new List<Point>();

                    int fftPointIndex = 0;

                    complexBuffer = ArrayPool<Complex>.Shared.Rent(fft_samples);

                    int complexBufferIndex = 0;

                    Point point = new Point();

                    int pointSamples = 0;

                    int m = (int)Math.Log(fft_samples, 2.0);

                    do
                    {
                        decoder.LoadFromStream(decoderData, out byte[] currentBytes);
                        int sampleIndex = 0;

                        unsafe
                        {
                            fixed (void* ptr = currentBytes)
                            {
                                float* currentFloats = (float*)ptr;
                                int currentFloatsLength = currentBytes.Length / bytes_per_sample;

                                while (sampleIndex < currentFloatsLength)
                                {
                                    // Each point is composed of multiple samples
                                    for (; pointSamples < samplesPerPoint && sampleIndex < currentFloatsLength; pointSamples += channels, sampleIndex += channels)
                                    {
                                        // Find the maximum amplitude for each channel in the point
                                        float left = *(currentFloats + sampleIndex);
                                        float right = *(currentFloats + sampleIndex + 1);

                                        point.AmplitudeLeft = Math.Max(point.AmplitudeLeft, Math.Abs(left));
                                        point.AmplitudeRight = Math.Max(point.AmplitudeRight, Math.Abs(right));

                                        complexBuffer[complexBufferIndex].X = (left + right) * 0.5f;
                                        complexBuffer[complexBufferIndex].Y = 0;

                                        if (++complexBufferIndex >= fft_samples)
                                        {
                                            complexBufferIndex = 0;

                                            FastFourierTransform.FFT(true, m, complexBuffer);

                                            point.LowIntensity = computeIntensity(sample_rate, complexBuffer, low_min, mid_min);
                                            point.MidIntensity = computeIntensity(sample_rate, complexBuffer, mid_min, high_min);
                                            point.HighIntensity = computeIntensity(sample_rate, complexBuffer, high_min, high_max);

                                            for (; fftPointIndex < pointList.Count; fftPointIndex++)
                                            {
                                                var prevPoint = pointList[fftPointIndex];
                                                prevPoint.LowIntensity = point.LowIntensity;
                                                prevPoint.MidIntensity = point.MidIntensity;
                                                prevPoint.HighIntensity = point.HighIntensity;
                                                pointList[fftPointIndex] = prevPoint;
                                            }

                                            fftPointIndex++; // current Point is going to be added
                                        }
                                    }

                                    if (pointSamples >= samplesPerPoint)
                                    {
                                        // There may be unclipped samples, so clip them ourselves
                                        point.AmplitudeLeft = Math.Min(1, point.AmplitudeLeft);
                                        point.AmplitudeRight = Math.Min(1, point.AmplitudeRight);

                                        pointList.Add(point);

                                        point = new Point();
                                        pointSamples = 0;
                                    }
                                }
                            }
                        }
                    } while (decoderData.Loading);

                    points = pointList.ToArray();
                }
                finally
                {
                    if (complexBuffer != null)
                        ArrayPool<Complex>.Shared.Return(complexBuffer);
                }
            }, cancelSource.Token);
        }

        private float computeIntensity(int frequency, Complex[] bins, float startFrequency, float endFrequency)
        {
            int startBin = (int)(fft_samples * startFrequency / frequency);
            int endBin = (int)(fft_samples * endFrequency / frequency);

            startBin = Math.Clamp(startBin, 0, fft_bins);
            endBin = Math.Clamp(endBin, 0, fft_bins);

            float value = 0;
            for (int i = startBin; i < endBin; i++)
                value += (float)Math.Sqrt(bins[i].X * bins[i].X + bins[i].Y * bins[i].Y);
            return value;
        }

        /// <summary>
        /// Creates a new <see cref="Waveform"/> containing a specific number of data points by selecting the average value of each sampled group.
        /// </summary>
        /// <param name="pointCount">The number of points the resulting <see cref="Waveform"/> should contain.</param>
        /// <param name="cancellationToken">The token to cancel the task.</param>
        /// <returns>An async task for the generation of the <see cref="Waveform"/>.</returns>
        public async Task<Waveform> GenerateResampledAsync(int pointCount, CancellationToken cancellationToken = default)
        {
            if (pointCount < 0) throw new ArgumentOutOfRangeException(nameof(pointCount));

            if (pointCount == 0)
                return new Waveform(null);

            await readTask.ConfigureAwait(false);

            return await Task.Run(() =>
            {
                var generatedPoints = new Point[pointCount];

                float pointsPerGeneratedPoint = (float)points.Length / pointCount;

                // Determines at which width (relative to the resolution) our smoothing filter is truncated.
                // Should not effect overall appearance much, except when the value is too small.
                // A gaussian contains almost all its mass within its first 3 standard deviations,
                // so a factor of 3 is a very good choice here.
                const int kernel_width_factor = 3;

                int kernelWidth = (int)(pointsPerGeneratedPoint * kernel_width_factor) + 1;

                float[] filter = new float[kernelWidth + 1];

                for (int i = 0; i < filter.Length; ++i)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return new Waveform(null);

                    filter[i] = (float)Blur.EvalGaussian(i, pointsPerGeneratedPoint);
                }

                // we're keeping two indices: one for the original (fractional!) point we're generating based on,
                // and one (integral) for the points we're going to be generating.
                // it's important to avoid adding by pointsPerGeneratedPoint in a loop, as floating-point errors can result in
                // drifting of the computed values in either direction - we multiply the generated index by pointsPerGeneratedPoint instead.
                float originalPointIndex = 0;
                int generatedPointIndex = 0;

                while (generatedPointIndex < pointCount)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return new Waveform(null);

                    int startIndex = (int)originalPointIndex - kernelWidth;
                    int endIndex = (int)originalPointIndex + kernelWidth;

                    var point = new Point();
                    float totalWeight = 0;

                    for (int j = startIndex; j < endIndex; j++)
                    {
                        if (j < 0 || j >= points.Length) continue;

                        float weight = filter[Math.Abs(j - startIndex - kernelWidth)];
                        totalWeight += weight;

                        point.AmplitudeLeft += weight * points[j].AmplitudeLeft;
                        point.AmplitudeRight += weight * points[j].AmplitudeRight;
                        point.LowIntensity += weight * points[j].LowIntensity;
                        point.MidIntensity += weight * points[j].MidIntensity;
                        point.HighIntensity += weight * points[j].HighIntensity;
                    }

                    if (totalWeight > 0)
                    {
                        // Means
                        point.AmplitudeLeft /= totalWeight;
                        point.AmplitudeRight /= totalWeight;
                        point.LowIntensity /= totalWeight;
                        point.MidIntensity /= totalWeight;
                        point.HighIntensity /= totalWeight;
                    }

                    generatedPoints[generatedPointIndex] = point;

                    generatedPointIndex += 1;
                    originalPointIndex = generatedPointIndex * pointsPerGeneratedPoint;
                }

                return new Waveform(null)
                {
                    points = generatedPoints,
                    channels = channels
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets all the points represented by this <see cref="Waveform"/>.
        /// </summary>
        public Point[] GetPoints() => GetPointsAsync().GetResultSafely();

        /// <summary>
        /// Gets all the points represented by this <see cref="Waveform"/>.
        /// </summary>
        public async Task<Point[]> GetPointsAsync()
        {
            await readTask.ConfigureAwait(false);
            return points;
        }

        /// <summary>
        /// Gets the number of channels represented by each <see cref="Point"/>.
        /// </summary>
        public int GetChannels() => GetChannelsAsync().GetResultSafely();

        /// <summary>
        /// Gets the number of channels represented by each <see cref="Point"/>.
        /// </summary>
        public async Task<int> GetChannelsAsync()
        {
            await readTask.ConfigureAwait(false);
            return channels;
        }

        #region Disposal

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool isDisposed;

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            cancelSource.Cancel();
            cancelSource.Dispose();
            points = Array.Empty<Point>();
        }

        #endregion

        /// <summary>
        /// Represents a singular point of data in a <see cref="Waveform"/>.
        /// </summary>
        public struct Point
        {
            /// <summary>
            /// The amplitude of the left channel.
            /// </summary>
            public float AmplitudeLeft;

            /// <summary>
            /// The amplitude of the right channel.
            /// </summary>
            public float AmplitudeRight;

            /// <summary>
            /// Unnormalised total intensity of the low-range (bass) frequencies.
            /// </summary>
            public float LowIntensity;

            /// <summary>
            /// Unnormalised total intensity of the mid-range frequencies.
            /// </summary>
            public float MidIntensity;

            /// <summary>
            /// Unnormalised total intensity of the high-range (treble) frequencies.
            /// </summary>
            public float HighIntensity;
        }
    }
}
