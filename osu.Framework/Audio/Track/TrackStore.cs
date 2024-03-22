﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Audio.Mixing;
using osu.Framework.IO.Stores;

namespace osu.Framework.Audio.Track
{
    internal class TrackStore : AudioCollectionManager<AdjustableAudioComponent>, ITrackStore
    {
        private readonly IResourceStore<byte[]> store;
        private readonly AudioMixer mixer;
        private readonly GetNewTrackDelegate getNewTrackDelegate;

        public delegate Track GetNewTrackDelegate(Stream dataStream, string name);

        internal TrackStore([NotNull] IResourceStore<byte[]> store, [NotNull] AudioMixer mixer, [NotNull] GetNewTrackDelegate getNewTrackDelegate)
        {
            this.store = store;
            this.mixer = mixer;
            this.getNewTrackDelegate = getNewTrackDelegate;

            (store as ResourceStore<byte[]>)?.AddExtension(@"mp3");
        }

        public Track GetVirtual(double length = double.PositiveInfinity, string name = "virtual")
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var track = new TrackVirtual(length, name);
            AddItem(track);
            return track;
        }

        public Track Get(string name)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (string.IsNullOrEmpty(name)) return null;

            var dataStream = store.GetStream(name);

            if (dataStream == null)
                return null;

            Track track = getNewTrackDelegate(dataStream, name);

            mixer.Add(track);
            AddItem(track);

            return track;
        }

        public Task<Track> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.Run(() => Get(name), cancellationToken);

        public Stream GetStream(string name) => store.GetStream(name);

        public IEnumerable<string> GetAvailableResources() => store.GetAvailableResources();
    }
}
