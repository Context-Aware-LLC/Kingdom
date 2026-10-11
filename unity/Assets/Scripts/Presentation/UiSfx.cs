using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// One-shot sounds for menu screens, outside any match. <see cref="CardSoundDirector"/> and
    /// <see cref="HeroVoiceDirector"/> live on the match's objects and die with it; this owns one
    /// persistent AudioSource so any screen can play a UI sound without owning audio itself.
    ///
    /// <para>Ids are file names under <c>Assets/Sound/SFX/</c> (the SoundEffects group, address
    /// <c>sfx/&lt;id&gt;</c>), so <c>_2</c>…<c>_6</c> variants work here exactly as they do for
    /// cards. Routed through the SFX mixer group, so the settings panel's volume and mute apply.</para>
    /// </summary>
    public static class UiSfx
    {
        public const string WaxStamp = "Wax_stamp";
        public const string ScrollUnfurls = "scroll-unfurls";

        private static AddressableSoundEffectPlayer _player;

        public static void Play(string soundId) => Player()?.Play(soundId);

        /// <summary>Load ahead of a <see cref="Play"/> that has to land on a specific frame.</summary>
        public static void Preload(string soundId) => Player()?.Preload(soundId);

        private static AddressableSoundEffectPlayer Player()
        {
            if (_player != null) return _player;
            if (!Application.isPlaying) return null;

            var host = new GameObject("[UiSfx]");
            Object.DontDestroyOnLoad(host);
            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = AudioSettings.SfxGroup;
            _player = new AddressableSoundEffectPlayer(source, owner: nameof(UiSfx));
            return _player;
        }

        /// <summary>Domain reload off (Enter Play Mode Options) keeps statics across plays; the
        /// host object does not survive, so drop the player with it.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _player = null;
    }
}
