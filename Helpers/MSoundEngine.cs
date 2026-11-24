/*
    AmplitudeSoundboard
    Copyright (C) 2021-2026 dan0v
    https://git.dan0v.com/AmplitudeSoundboard

    This file is part of AmplitudeSoundboard.

    AmplitudeSoundboard is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    AmplitudeSoundboard is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with AmplitudeSoundboard.  If not, see <https://www.gnu.org/licenses/>.
*/

using Amplitude.Models;
using Amplitude.ViewModels;
using ManagedBass;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Timers;

namespace Amplitude.Helpers
{
    class MSoundEngine : ISoundEngine
    {
        private readonly Lazy<ConfigManager> _configManager;
        private readonly Lazy<WindowManager> _windowManager;

        private ConfigManager ConfigManager => _configManager.Value;
        private WindowManager WindowManager => _windowManager.Value;

        private readonly object currentlyPlayingLock = new();
        private ObservableCollection<PlayingClip> _currentlyPlaying = [];
        public ObservableCollection<PlayingClip> CurrentlyPlaying => _currentlyPlaying;

        private readonly object queueLock = new();
        private ObservableCollection<SoundClip> _queued = [];
        public ObservableCollection<SoundClip> Queued => _queued;

        private readonly object streamsToFreeLock = new();
        private Dictionary<int, StreamToFree> _streamsToFree = [];


        private const long TIMER_MS = 100;
        private readonly Timer timer = new(TIMER_MS)
        {
            AutoReset = true,
        };

        private void RefreshPlaybackProgressAndCheckQueue(object? sender, ElapsedEventArgs e)
        {
            foreach (var track in CurrentlyPlaying.ToArray())
            {
                // Timer ticks are shared by all clips and need not coincide with
                // their start times. Read the actual playback position instead
                // of estimating elapsed audio from the number of ticks.
                var position = Bass.ChannelGetPosition(track.BassStreamId);
                if (position >= 0)
                {
                    track.CurrentPos = Bass.ChannelBytes2Seconds(track.BassStreamId, position);
                }

                if (Bass.ChannelIsActive(track.BassStreamId) == PlaybackState.Stopped)
                {
                    if (track.LoopClip)
                    {
                        track.CurrentPos = 0;
                        Bass.ChannelPlay(track.BassStreamId, true);
                    }
                    else
                    {
                        // Only release a naturally ending clip after BASS has
                        // finished playing it, including its playback buffer.
                        StopPlaying(track.BassStreamId, 0, 0);
                    }
                }
                else if (!track.LoopClip && track.FadeOutMilis > 0
                    && track.RemainingMilis <= track.FadeOutMilis)
                {
                    StopPlaying(track.BassStreamId, track.RemainingMilis, track.FadeOutMilis);
                }
            }
            lock (queueLock)
            {
                if (!CurrentlyPlaying.Any() && Queued.Any())
                {
                    var clip = Queued[0];
                    Play(clip, true);
                    Queued.RemoveAt(0);
                }
            }
            lock (streamsToFreeLock)
            {
                var timeNow = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var streamsToFreeAndRemove = _streamsToFree.Values.Where(it => timeNow >= it.freeAtUnixTime).ToArray();
                foreach (StreamToFree stream in streamsToFreeAndRemove)
                {
                    StopPlaying(stream.bassStreamId, 0, 0);
                    _streamsToFree.Remove(stream.bassStreamId);
                }
            }
        }

        public const int SAMPLE_RATE = 44100;

        private readonly object bass_lock = new();
        private readonly Dictionary<string, string> deviceUids = new();
        private DateTimeOffset lastPlayUtc;
        private volatile bool recovering;

        internal string DeviceSnapshot()
        {
            lock (bass_lock)
            {
                var devices = new List<string>();
                for (int i = 1; Bass.GetDeviceInfo(i, out var info); i++)
                    devices.Add($"{i}:{info.Driver}:{info.Name}:{info.IsEnabled}:{info.IsDefault}");
                return string.Join("\n", devices);
            }
        }

        // Called only while idle. Release this application's contexts, never
        // the system devices or another application's microphone capture.
        internal bool RecoverOutputs(IEnumerable<string> names)
        {
            lock (bass_lock)
            {
                if (CurrentlyPlaying.Any() || Queued.Any()) return false;
                recovering = true;
                try
                {
                    lock (streamsToFreeLock) _streamsToFree.Clear();
                    var initialized = new List<int>();
                    for (int i = 1; Bass.GetDeviceInfo(i, out var info); i++)
                        if (info.IsInitialized) initialized.Add(i);
                    foreach (int id in initialized)
                    {
                        Bass.CurrentDevice = id;
                        Bass.Free();
                    }
                    bool ready = true;
                    foreach (var name in names.Distinct())
                    {
                        var id = GetOutputPlayerByName(name);
                        if (!id.HasValue || !EnsureOutput(id.Value)) ready = false;
                    }
                    return ready;
                }
                finally { recovering = false; }
            }
        }

        private static bool EnsureOutput(int id)
        {
            if (!Bass.Init(id, SAMPLE_RATE) && Bass.LastError != Errors.Already) return false;
            Bass.CurrentDevice = id;
            return Bass.CurrentDevice == id && Bass.Start();
        }


        public List<string> OutputDeviceListWithoutGlobal
        {
            get
            {
                var all = OutputDeviceListWithGlobal.ToList();
                all.Remove(ISoundEngine.DEFAULT_DEVICE_NAME);
                all.Remove("System default");
                return all;
            }
        }

        public List<string> OutputDeviceListWithGlobal
        {
            get
            {
                List<string> devices = [];
                // Index 0 is "No Sound", so skip
                for (int dev = 1; Bass.GetDeviceInfo(dev, out DeviceInfo info); dev++)
                {
                    if (info.IsEnabled) devices.Add(info.Name);
                }
                return devices;
            }
        }

        private int? GetOutputPlayerByName(string playerDeviceName)
        {
            if (playerDeviceName == ISoundEngine.GLOBAL_DEFAULT_DEVICE_NAME)
            {
                playerDeviceName = ISoundEngine.DEFAULT_DEVICE_NAME;
            }

            if (playerDeviceName == ISoundEngine.DEFAULT_DEVICE_NAME || playerDeviceName == "System default")
            {
                return 1;
            }

            lock (bass_lock)
            {
                var candidates = new List<(int Id, DeviceInfo Info)>();
                for (int n = 1; Bass.GetDeviceInfo(n, out var info); n++)
                {
                    if (info.IsEnabled) candidates.Add((n, info));
                }
                if (deviceUids.TryGetValue(playerDeviceName, out var uid))
                {
                    var known = candidates.FirstOrDefault(d => d.Info.Driver == uid);
                    if (known.Id != 0) return known.Id;
                    // A disconnected saved endpoint must not resolve to an
                    // unrelated device that happens to have the same name.
                    return null;
                }
                var match = candidates.FirstOrDefault(d => d.Info.Name == playerDeviceName);
                if (match.Id != 0)
                {
                    if (!string.IsNullOrEmpty(match.Info.Driver)) deviceUids[playerDeviceName] = match.Info.Driver;
                    return match.Id;
                }
            }
            return null;
        }

        public MSoundEngine(Lazy<ConfigManager> configManager, Lazy<WindowManager> windowManager)
        {
            _configManager = configManager;
            _windowManager = windowManager;
            timer.Elapsed += RefreshPlaybackProgressAndCheckQueue;
            timer.Start();
        }

        public void AddToQueue(SoundClip source)
        {
            lock (queueLock)
            {
                Queued.Add(source.ShallowCopy());
            }
        }

        private void StopAndRemoveFromQueue(string id)
        {
            lock (queueLock)
            {
                foreach (var clip in Queued.Where(clip => clip.Id == id).ToArray())
                {
                    Queued.Remove(clip);
                }
            }
            foreach (var clip in CurrentlyPlaying.Where(clip => clip.SoundClipId == id).ToArray())
            {
                StopPlaying(clip.BassStreamId, clip.RemainingMilis, clip.FadeOutMilis);
            }
        }

        private bool ClipPlayingOrQueued(string id)
        {
            lock (queueLock)
            {
                if (Queued.Any(clip => clip.Id == id))
                {
                    return true;
                }
            }
            lock (currentlyPlayingLock)
            {
                if (CurrentlyPlaying.Any(clip => clip.SoundClipId == id))
                {
                    return true;
                }
            }
            return false;
        }

        public void Play(SoundClip source, bool fromQueue = false)
        {
            if (recovering) return;
            var tempId = source.Id ?? source.AudioFilePath;
            if (!fromQueue && ClipPlayingOrQueued(tempId))
            {
                if (ConfigManager.Config.StopAudioOnRepeatTrigger)
                {
                    StopAndRemoveFromQueue(tempId);
                }
                // With repeat-to-stop disabled, let the existing playback finish
                // rather than starting overlapping copies of the same clip.
                return;
            }

            if (!BrowseIO.ValidAudioFile(source.AudioFilePath, true, source))
            {
                return;
            }

            var outputs = source.OutputSettingsFromProfile.ToArray();
            lock (bass_lock)
            {
                var now = DateTimeOffset.UtcNow;
                // Some Bluetooth failures leave the device list unchanged.
                // Reopen once on the first trigger after a quiet minute rather
                // than keeping the headset artificially active with silence.
                if (lastPlayUtc != default && now - lastPlayUtc > TimeSpan.FromMinutes(1) && !MacSession.IsLocked())
                    RecoverOutputs(outputs.Select(s => s.DeviceName));
                lastPlayUtc = now;
            }
            foreach (OutputSettings settings in outputs)
            {
                Play(source.AudioFilePath, settings.Volume, source.Volume, settings.DeviceName, source.LoopClip, tempId, settings.FadeOutMilis, source.Name);
            }
        }

        private void Play(string fileName, int volume, int volumeMultiplier, string playerDeviceName, bool loopClip, string soundClipId, int fadeOutMilis, string? name = null)
        {
            double vol = (volume / 100.0) * (volumeMultiplier / 100.0);

            int? devId = GetOutputPlayerByName(playerDeviceName);

            if (!devId.HasValue)
            {
                WindowManager.ShowErrorString(string.Format(Localization.Localizer.Instance["MissingDeviceString"], playerDeviceName));
                return;
            }

            bool streamError = false;
            bool bassError = false;

            lock (bass_lock)
            {
                // Init device
                bool initialized = EnsureOutput(devId.Value);
                if (!initialized && !CurrentlyPlaying.Any(c => c.OutputDevice == playerDeviceName))
                {
                    if (Bass.GetDeviceInfo(devId.Value, out var device) && device.IsInitialized)
                    {
                        Bass.CurrentDevice = devId.Value;
                        if (Bass.CurrentDevice == devId.Value) Bass.Free();
                    }
                    initialized = EnsureOutput(devId.Value);
                }
                if (initialized)
                {
                    int stream = Bass.CreateStream(fileName);

                    Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, vol);

                    // Retry a failed start once with a fresh output context.
                    // Ordinary streams already mix and resample in BASS; the
                    // previous unused mixer also leaked a stream per trigger.
                    bool started = stream != 0 && Bass.ChannelPlay(stream, false);
                    if (!started && !CurrentlyPlaying.Any(c => c.OutputDevice == playerDeviceName))
                    {
                        if (stream != 0) Bass.StreamFree(stream);
                        Bass.Free();
                        if (EnsureOutput(devId.Value))
                        {
                            stream = Bass.CreateStream(fileName);
                            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, vol);
                            started = stream != 0 && Bass.ChannelPlay(stream, false);
                        }
                    }

                    if (started)
                    {
                        // Track active streams so they can be stopped
                        try
                        {
                            var len = Bass.ChannelGetLength(stream, PositionFlags.Bytes);
                            double length = Bass.ChannelBytes2Seconds(stream, len);
                            PlayingClip track = new(
                                string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(fileName) ?? "" : name,
                                soundClipId,
                                playerDeviceName,
                                stream,
                                length,
                                loopClip,
                                fadeOutMilis
                                );

                            // A timer tick must not see a not-yet-started stream
                            // as a naturally completed one.
                            lock (currentlyPlayingLock)
                            {
                                CurrentlyPlaying.Add(track);
                            }
                        }
                        catch (Exception)
                        {
                            Bass.StreamFree(stream);
                            WindowManager.ShowErrorString(string.Format(Localization.Localizer.Instance["FileBadFormatString"], fileName));
                        }
                    }
                    else
                    {
                        if (stream != 0) Bass.StreamFree(stream);
                        streamError = true;
                    }
                }
                else
                {
                    bassError = true;
                }
            }
            if (streamError)
            {
                WindowManager.ShowErrorString($"Stream error: {Bass.LastError}");
            }
            if (bassError)
            {
                WindowManager.ShowErrorString($"ManagedBass error: {Bass.LastError}");
            }
        }

        public void CheckDeviceExistsAndGenerateErrors(OutputProfile profile)
        {
            foreach (OutputSettings settings in profile.OutputSettings)
            {
                if (GetOutputPlayerByName(settings.DeviceName) == null)
                {
                    if (profile != null)
                    {
                        WindowManager.ShowErrorOutputProfile(profile, ErrorListViewModel.OutputProfileErrorType.MISSING_DEVICE, settings.DeviceName);
                    }
                }
            }
        }

        public void Reset()
        {
            lock (queueLock)
            {
                Queued.Clear();
            }
            foreach (var stream in CurrentlyPlaying.ToArray())
            {
                var timeRemainingMilis = stream.RemainingMilis;
                var fadeOutDuration = timeRemainingMilis < stream.FadeOutMilis ? timeRemainingMilis : stream.FadeOutMilis;
                StopPlaying(stream.BassStreamId, timeRemainingMilis, (int)fadeOutDuration);
            }
        }

        public void StopPlaying(int handle, double remainingMilis, int fadeOutMilis, bool stopped = false)
        {
            if (fadeOutMilis == 0 || stopped)
            {
                lock (streamsToFreeLock)
                {
                    _streamsToFree.Remove(handle);
                }
                Bass.StreamFree(handle);
                lock (currentlyPlayingLock)
                {
                    PlayingClip? track = CurrentlyPlaying.FirstOrDefault(c => c.BassStreamId == handle);
                    if (track != null)
                    {
                        CurrentlyPlaying.Remove(track);
                    }
                }
            }
            else
            {
                lock (streamsToFreeLock)
                {
                    // Repeated timer ticks must not restart the same fade.
                    if (_streamsToFree.ContainsKey(handle))
                    {
                        return;
                    }
                    int remainingFadeOut = (int)(remainingMilis < fadeOutMilis ? remainingMilis : fadeOutMilis);
                    Bass.ChannelSlideAttribute(handle, ChannelAttribute.Volume, 0, remainingFadeOut);
                    _streamsToFree.Add(handle, new StreamToFree(handle, DateTimeOffset.Now.ToUnixTimeMilliseconds() + remainingFadeOut));
                }
            }
        }

        public void RemoveFromQueue(SoundClip clip)
        {
            lock (queueLock)
            {
                Queued.Remove(clip);
            }
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Elapsed -= RefreshPlaybackProgressAndCheckQueue;
            Reset();
            Bass.Free();
        }

        private class StreamToFree
        {
            public int bassStreamId;
            public long freeAtUnixTime;

            public StreamToFree(int bassStreamId, long freeAtUnixTime)
            {
                this.bassStreamId = bassStreamId;
                this.freeAtUnixTime = freeAtUnixTime;
            }
        }
    }
}
