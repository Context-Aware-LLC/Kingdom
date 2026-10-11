using UnityEngine;
using UnityEngine.Audio;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Player-controlled audio: one Mute switch plus independent Music and SFX volumes, persisted
    /// in PlayerPrefs and pushed to <c>Resources/Audio/MasterMixer.mixer</c>'s exposed parameters.
    /// Bound by <see cref="MatchSettingsController.BindAudioSettings"/> (Sam, 2026-09-05).
    ///
    /// <para><see cref="MusicGroup"/> is what <see cref="MusicDirector"/> routes its looping
    /// ambient-track player through; <see cref="SfxGroup"/> is what CardSoundDirector and
    /// HeroVoiceDirector route their AudioSources through.</para>
    ///
    /// <para>A missing mixer asset must cost volume control, never sound itself — every accessor
    /// degrades to "persist the number, apply nothing" rather than throwing.</para>
    /// </summary>
    public static class AudioSettings
    {
        private const string MixerResourcePath = "Audio/MasterMixer";
        private const string MusicGroupName    = "Music";
        private const string SfxGroupName      = "SFX";
        private const string MusicParam        = "MusicVolume";
        private const string SfxParam          = "SfxVolume";

        private const string MutedKey       = "audio.muted";
        private const string MusicVolumeKey = "audio.music_volume";
        private const string SfxVolumeKey   = "audio.sfx_volume";

        // An exposed AudioMixer parameter is in decibels. -80 is the floor a fader in this engine
        // reads as true silence; log10(0) is undefined, so 0 volume maps here rather than through
        // the formula below.
        //
        // This number is also MasterMixer's m_SuspendThreshold, which is why that asset must keep
        // m_EnableSuspend at 0 ("Auto Mixer Suspend" unticked in the inspector). Muting drives the
        // group to exactly the threshold, so with suspend on, mute stops the mixer's DSP rather
        // than attenuating it — and a source through a suspended mixer no longer reports itself as
        // playing, which is the one condition MusicDirector.Update() treats as "track finished,
        // start the next one". Ticking that box turns Mute into a silent race through the playlist.
        private const float SilenceDb = -80f;

        private static bool _loaded;
        private static bool _muted;
        private static float _musicVolume = 1f;
        private static float _sfxVolume = 1f;

        private static bool _mixerLoadAttempted;
        private static AudioMixer _mixer;

        public static bool  Muted        { get { EnsureLoaded(); return _muted; } }
        public static float MusicVolume  { get { EnsureLoaded(); return _musicVolume; } }
        public static float SfxVolume    { get { EnsureLoaded(); return _sfxVolume; } }

        /// <summary>The group every one-shot SFX AudioSource should route through. Null until the
        /// mixer asset resolves. Routes through <see cref="EnsureLoaded"/>, not just
        /// <see cref="EnsureMixer"/> — a director Awake()-ing before any settings panel has mounted
        /// must not be able to route a source through the mixer before the persisted volume/mute
        /// has ever been pushed to it.</summary>
        public static AudioMixerGroup SfxGroup   { get { EnsureLoaded(); return FindGroup(SfxGroupName); } }

        /// <summary>The group a future music player should route through. Null until the mixer
        /// asset resolves — see the class remarks on why nothing plays through it yet.</summary>
        public static AudioMixerGroup MusicGroup { get { EnsureLoaded(); return FindGroup(MusicGroupName); } }

        /// <summary>Boot-time safety net: read and (re)apply persisted audio settings once the first
        /// scene is up, regardless of whether that scene ever mounts a settings panel — Account
        /// Create and Character Create do not (Sam, 2026-09-05).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ApplyAtBoot() => EnsureLoaded();

        public static void SetMuted(bool muted)
        {
            EnsureLoaded();
            _muted = muted;
            PlayerPrefs.SetInt(MutedKey, muted ? 1 : 0);
            PlayerPrefs.Save();
            Apply();
        }

        public static void SetMusicVolume(float linear01)
        {
            EnsureLoaded();
            _musicVolume = Mathf.Clamp01(linear01);
            PlayerPrefs.SetFloat(MusicVolumeKey, _musicVolume);
            PlayerPrefs.Save();
            Apply();
        }

        public static void SetSfxVolume(float linear01)
        {
            EnsureLoaded();
            _sfxVolume = Mathf.Clamp01(linear01);
            PlayerPrefs.SetFloat(SfxVolumeKey, _sfxVolume);
            PlayerPrefs.Save();
            Apply();
        }

        /// <summary>Read persisted values (once) and push them to the mixer (every call). Genuinely
        /// idempotent — re-applying costs nothing — because the first push, made whenever a scene's
        /// settings panel first touches this class, can race the mixer's own native init and be
        /// silently dropped. Nothing else ever retries it: without this, a session where that race
        /// is lost stays unmuted until the player manually toggles Mute, since <see cref="_loaded"/>
        /// would otherwise gate <see cref="Apply"/> to a single attempt for the whole process
        /// lifetime. Every scene's settings mount calling this on Init is what gives the value
        /// another chance to stick.</summary>
        public static void EnsureLoaded()
        {
            if (!_loaded)
            {
                _loaded = true;
                _muted       = PlayerPrefs.GetInt(MutedKey, 0) != 0;
                _musicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(MusicVolumeKey, 1f));
                _sfxVolume   = Mathf.Clamp01(PlayerPrefs.GetFloat(SfxVolumeKey, 1f));
            }
            Apply();
        }

        private static void Apply()
        {
            EnsureMixer();
            if (_mixer == null) return;
            _mixer.SetFloat(MusicParam, _muted ? SilenceDb : LinearToDb(_musicVolume));
            _mixer.SetFloat(SfxParam,   _muted ? SilenceDb : LinearToDb(_sfxVolume));
        }

        private static void EnsureMixer()
        {
            if (_mixerLoadAttempted) return;
            _mixerLoadAttempted = true;
            _mixer = Resources.Load<AudioMixer>(MixerResourcePath);
            if (_mixer == null)
                Debug.LogWarning($"[AudioSettings] no AudioMixer at Resources/{MixerResourcePath} — " +
                                 "volume/mute will persist but nothing routes through it.");
        }

        private static AudioMixerGroup FindGroup(string groupName)
        {
            EnsureMixer();
            if (_mixer == null) return null;
            AudioMixerGroup[] matches = _mixer.FindMatchingGroups(groupName);
            return matches.Length > 0 ? matches[0] : null;
        }

        private static float LinearToDb(float linear01) =>
            linear01 <= 0.0001f ? SilenceDb : Mathf.Log10(linear01) * 20f;
    }
}
