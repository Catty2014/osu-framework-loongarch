﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using SoundTouch;

namespace osu.Framework.Audio.Track
{
    internal class TempoSDL2AudioPlayer : TrackSDL2AudioPlayer
    {
        private SoundTouchProcessor? soundTouch;

        private double tempo = 1;

        /// <summary>
        /// Represents current speed.
        /// </summary>
        public double Tempo
        {
            get => tempo;
            set => setTempo(value);
        }

        private readonly int samplesize;

        private bool doneFilling;
        private bool donePlaying;

        public override bool Done => base.Done && (soundTouch == null || donePlaying);

        /// <summary>
        /// Creates a new <see cref="TempoSDL2AudioPlayer"/>.
        /// </summary>
        /// <param name="rate"><inheritdoc /></param>
        /// <param name="channels"><inheritdoc /></param>
        /// <param name="samples"><see cref="TempoSDL2AudioPlayer"/> will prepare this amount of samples (or more) on every update.</param>
        public TempoSDL2AudioPlayer(int rate, byte channels, int samples)
            : base(rate, channels)
        {
            samplesize = samples;
        }

        public void FillRequiredSamples() => fillSamples(samplesize);

        /// <summary>
        /// Fills SoundTouch buffer until it has a specific amount of samples.
        /// </summary>
        /// <param name="samples">Needed sample count</param>
        private void fillSamples(int samples)
        {
            if (soundTouch == null)
                return;

            while (!base.Done && soundTouch.AvailableSamples < samples)
            {
                int getSamples = (int)Math.Ceiling((samples - soundTouch.AvailableSamples) * Tempo) * SrcChannels;
                float[] src = new float[getSamples];
                getSamples = base.GetRemainingSamples(src);
                if (getSamples <= 0)
                    break;

                soundTouch.PutSamples(src, getSamples / SrcChannels);
            }

            if (!doneFilling && base.Done)
            {
                soundTouch.Flush();
                doneFilling = true;
            }
        }

        /// <summary>
        /// Sets tempo. This initializes <see cref="soundTouch"/> if it's set to some value else than 1.0, and once it's set again to 1.0, it disposes <see cref="soundTouch"/>.
        /// </summary>
        /// <param name="tempo">New tempo value</param>
        private void setTempo(double tempo)
        {
            if (soundTouch == null && tempo == 1.0f)
                return;

            if (soundTouch == null)
            {
                soundTouch = new SoundTouchProcessor
                {
                    SampleRate = SrcRate,
                    Channels = SrcChannels
                };
                soundTouch.SetSetting(SettingId.UseQuickSeek, 1);
                soundTouch.SetSetting(SettingId.OverlapDurationMs, 4);
                soundTouch.SetSetting(SettingId.SequenceDurationMs, 30);
            }

            if (this.tempo != tempo)
            {
                this.tempo = tempo;

                if (Tempo == 1.0f)
                {
                    if (AudioData != null)
                    {
                        int latency = GetTempoLatencyInSamples() * 4 * SrcChannels;
                        long temp = !ReversePlayback ? AudioData.Position - latency : AudioData.Position + latency;

                        if (temp >= 0)
                            AudioData.Position = temp;
                    }

                    Reset(false);
                    soundTouch = null;
                    return;
                }

                double tempochange = Math.Clamp((Math.Abs(tempo) - 1.0d) * 100.0d, -95, 5000);
                soundTouch.TempoChange = tempochange;
                FillRequiredSamples();
            }
        }

        /// <summary>
        /// Returns tempo and rate adjusted audio samples. It calls a parent method if <see cref="Tempo"/> is 1.
        /// </summary>
        /// <param name="ret">An array to put samples in</param>
        /// <returns>The number of samples put</returns>
        public override int GetRemainingSamples(float[] ret)
        {
            if (soundTouch == null)
                return base.GetRemainingSamples(ret);

            if (RelativeRate == 0)
                return 0;

            int expected = ret.Length / SrcChannels;

            if (!doneFilling && soundTouch.AvailableSamples < expected)
            {
                fillSamples(expected);
            }

            int got = soundTouch.ReceiveSamples(ret, expected);

            if (got == 0 && doneFilling)
                donePlaying = true;

            return got * SrcChannels;
        }

        public override void Reset(bool resetPosition = true)
        {
            base.Reset(resetPosition);
            soundTouch?.Flush();
            doneFilling = false;
            donePlaying = false;
        }

        protected int GetTempoLatencyInSamples()
        {
            if (soundTouch == null)
                return 0;

            return (int)(soundTouch.UnprocessedSampleCount + soundTouch.AvailableSamples * Tempo);
        }

        protected override double GetProcessingLatency() => base.GetProcessingLatency() + (double)GetTempoLatencyInSamples() / SrcRate * 1000.0d;

        public override void Seek(double seek)
        {
            base.Seek(seek);
            if (soundTouch != null)
                FillRequiredSamples();
        }
    }
}
