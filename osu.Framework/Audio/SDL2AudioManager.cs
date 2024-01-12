﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using osu.Framework.Audio.Callbacks;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.SDL2;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Threading;
using SDL2;

namespace osu.Framework.Audio
{
    public class SDL2AudioManager : AudioManager
    {
        public const int AUDIO_FREQ = 44100;
        public const byte AUDIO_CHANNELS = 2;
        public const ushort AUDIO_FORMAT = SDL.AUDIO_F32;

        private volatile uint deviceId;

        private SDL.SDL_AudioSpec spec;

        private static readonly AudioDecoderManager decoder = new AudioDecoderManager();

        private readonly List<SDL2AudioMixer> sdlMixerList = new List<SDL2AudioMixer>();

        private readonly SDL2AudioCallback audioCallback;

        private Scheduler eventScheduler => EventScheduler ?? CurrentAudioThread.Scheduler;

        protected override void InvokeOnNewDevice(string deviceName) => eventScheduler.Add(() => base.InvokeOnNewDevice(deviceName));

        protected override void InvokeOnLostDevice(string deviceName) => eventScheduler.Add(() => base.InvokeOnLostDevice(deviceName));

        /// <summary>
        /// Creates a new <see cref="SDL2AudioManager"/>.
        /// </summary>
        /// <param name="audioThread">The host's audio thread.</param>
        /// <param name="trackStore">The resource store containing all audio tracks to be used in the future.</param>
        /// <param name="sampleStore">The sample store containing all audio samples to be used in the future.</param>
        public SDL2AudioManager(AudioThread audioThread, ResourceStore<byte[]> trackStore, ResourceStore<byte[]> sampleStore)
            : base(audioThread, trackStore, sampleStore)
        {
            audioCallback = new SDL2AudioCallback((_, stream, size) => internalAudioCallback(stream, size));

            // Must not edit this except for samples, as components (especially mixer) expects this to match.
            spec = new SDL.SDL_AudioSpec
            {
                freq = AUDIO_FREQ,
                channels = AUDIO_CHANNELS,
                format = AUDIO_FORMAT,
                callback = audioCallback.Callback,
                userdata = audioCallback.Handle,
                samples = 256 // determines latency, this value can be changed but is already reasonably low
            };

            AudioScheduler.Add(() =>
            {
                updateDeviceNames();

                // comment below lines if you want to use FFmpeg to decode audio, AudioDecoder will use FFmpeg if no BASS device is available
                ManagedBass.Bass.Configure((ManagedBass.Configuration)68, 1);
                audioThread.InitDevice(ManagedBass.Bass.NoSoundDevice);
            });
        }

        private string currentDeviceName = "Not loaded";

        public override string ToString()
        {
            return $@"{GetType().ReadableName()} ({currentDeviceName})";
        }

        protected override AudioMixer AudioCreateAudioMixer(AudioMixer fallbackMixer, string identifier)
        {
            var mixer = new SDL2AudioMixer(fallbackMixer, identifier);
            AddItem(mixer);
            return mixer;
        }

        protected override void ItemAdded(AudioComponent item)
        {
            base.ItemAdded(item);

            if (item is SDL2AudioMixer mixer)
            {
                try
                {
                    if (deviceId != 0)
                        SDL.SDL_LockAudioDevice(deviceId);

                    sdlMixerList.Add(mixer);
                }
                finally
                {
                    if (deviceId != 0)
                        SDL.SDL_UnlockAudioDevice(deviceId);
                }
            }
        }

        protected override void ItemRemoved(AudioComponent item)
        {
            base.ItemRemoved(item);

            if (item is SDL2AudioMixer mixer)
            {
                try
                {
                    if (deviceId != 0)
                        SDL.SDL_LockAudioDevice(deviceId);

                    sdlMixerList.Remove(mixer);
                }
                finally
                {
                    if (deviceId != 0)
                        SDL.SDL_UnlockAudioDevice(deviceId);
                }
            }
        }

        private void internalAudioCallback(IntPtr stream, int bufsize)
        {
            try
            {
                float[] main = new float[bufsize / 4];

                foreach (var mixer in sdlMixerList)
                {
                    if (mixer.IsAlive)
                        mixer.MixChannelsInto(main);
                }

                unsafe
                {
                    fixed (float* mainPtr = main)
                        Buffer.MemoryCopy(mainPtr, stream.ToPointer(), bufsize, bufsize);
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error while pushing audio to SDL");
            }
        }

        internal void OnNewDeviceEvent(int addedDeviceIndex)
        {
            AudioScheduler.Add(() =>
            {
                // the index is only vaild until next SDL_GetNumAudioDevices call, so get the name first.
                string name = SDL.SDL_GetAudioDeviceName(addedDeviceIndex, 0);

                updateDeviceNames();
                InvokeOnNewDevice(name);
            });
        }

        internal void OnLostDeviceEvent(uint removedDeviceId)
        {
            AudioScheduler.Add(() =>
            {
                // SDL doesn't retain information about removed device.
                updateDeviceNames();

                if (deviceId == removedDeviceId) // current device lost
                {
                    InvokeOnLostDevice(currentDeviceName);
                    SetAudioDevice();
                }
                else
                {
                    // we can probably guess the name by comparing the old list and the new one, but it won't be reliable
                    InvokeOnLostDevice(string.Empty);
                }
            });
        }

        private void updateDeviceNames() => DeviceNames = EnumerateAllDevices().ToImmutableList();

        protected virtual IEnumerable<string> EnumerateAllDevices()
        {
            int deviceCount = SDL.SDL_GetNumAudioDevices(0); // it may return -1 if only default device is available (sound server)
            for (int i = 0; i < deviceCount; i++)
                yield return SDL.SDL_GetAudioDeviceName(i, 0);
        }

        protected override bool SetAudioDevice(string deviceName = null)
        {
            if (!DeviceNames.Contains(deviceName))
                deviceName = null;

            if (deviceId > 0)
                SDL.SDL_CloseAudioDevice(deviceId);

            Logger.Log("Trying this device: " + deviceName);

            // Let audio driver adjust latency, this may set to a high value on Windows (but usually around 10ms), but let's just be safe
            const uint flag = SDL.SDL_AUDIO_ALLOW_SAMPLES_CHANGE;
            deviceId = SDL.SDL_OpenAudioDevice(deviceName, 0, ref spec, out var outspec, (int)flag);

            if (deviceId == 0)
            {
                if (deviceName == null)
                {
                    Logger.Log("No audio device can be used! Check your audio system.", level: LogLevel.Error);
                    return false;
                }

                Logger.Log("SDL Audio init failed, try using default device...", level: LogLevel.Important);
                return SetAudioDevice();
            }

            spec = outspec;

            // Start playback
            SDL.SDL_PauseAudioDevice(deviceId, 0);

            currentDeviceName = deviceName ?? "Default";

            Logger.Log($@"🔈 SDL Audio initialised
                            Driver:      {SDL.SDL_GetCurrentAudioDriver()}
                            Device Name: {currentDeviceName}
                            Format:      {spec.freq}hz {spec.channels}ch
                            Resolution:  {(SDL.SDL_AUDIO_ISUNSIGNED(spec.format) ? "unsigned " : "")}{SDL.SDL_AUDIO_BITSIZE(spec.format)}bit{(SDL.SDL_AUDIO_ISFLOAT(spec.format) ? " float" : "")}
                            Samples:     {spec.samples} samples");

            return true;
        }

        protected override bool SetAudioDevice(int deviceIndex)
        {
            if (deviceIndex < DeviceNames.Count && deviceIndex >= 0)
                return SetAudioDevice(DeviceNames[deviceIndex]);

            return SetAudioDevice();
        }

        protected override bool IsCurrentDeviceValid() => SDL.SDL_GetAudioDeviceStatus(deviceId) != SDL.SDL_AudioStatus.SDL_AUDIO_STOPPED;

        internal override Track.Track GetNewTrack(Stream data, string name)
        {
            TrackSDL2 track = new TrackSDL2(name, spec.freq, spec.channels, spec.samples);
            EnqueueAction(() => decoder.StartDecodingAsync(AUDIO_FREQ, AUDIO_CHANNELS, AUDIO_FORMAT, data, track.ReceiveAudioData));
            return track;
        }

        internal override SampleFactory GetSampleFactory(Stream data, string name, AudioMixer mixer, int playbackConcurrency)
            => new SampleSDL2Factory(data, name, (SDL2AudioMixer)mixer, playbackConcurrency, spec);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            decoder?.Dispose();

            if (deviceId > 0)
            {
                SDL.SDL_CloseAudioDevice(deviceId);
                deviceId = 0;
            }

            audioCallback?.Dispose();
        }
    }
}
