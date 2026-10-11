using System.Collections.Generic;
using Kingdom.Core.Contracts;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Kingdom.Presentation
{
    /// <summary>Plays card-authored sound effects from the match's presentation event stream.</summary>
    public sealed class CardSoundDirector : MonoBehaviour
    {
        private CardSoundCueRouter _router;
        private AddressableSoundEffectPlayer _player;

        public void Init(ICardDatabase database)
        {
            _router = new CardSoundCueRouter(database);
            // TryGetComponent, not `??`: the null-coalescing operator is the C# null check and
            // bypasses UnityEngine.Object's overloaded `==`, so GetComponent's fake-null wrapper
            // reads as non-null, AddComponent never runs, and the next line throws
            // MissingComponentException (Sam, 2026-08-27).
            if (!TryGetComponent(out AudioSource source)) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = AudioSettings.SfxGroup;
            _player = new AddressableSoundEffectPlayer(source);
        }

        public void OnStateUpdated(GameState state) => _router?.Capture(state);

        public void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            if (_router == null || _player == null || batch == null) return;
            foreach (GameEvent matchEvent in batch)
                foreach (string soundId in _router.Route(matchEvent))
                    _player.Play(soundId);
        }

        /// <summary>The end-of-match stinger (Sam, 2026-09-28). A draw has none. Variants
        /// (<c>match_victory_2</c>, <c>match_defeat_3</c>…) are picked up by
        /// <see cref="AddressableSoundEffectPlayer.Play"/>.</summary>
        public void OnMatchEnded(MatchOutcome outcome, int localSeat)
        {
            if (_player == null) return;
            if (outcome == MatchOutcome.Draw || outcome == MatchOutcome.InProgress) return;
            bool won = outcome == (localSeat == 0 ? MatchOutcome.Player0Win : MatchOutcome.Player1Win);
            _player.Play(won ? "match_victory" : "match_defeat");
        }

        private void OnDestroy() => _player?.ReleaseAll();
    }

    /// <summary>
    /// Plays a clip by Addressables key, caching the load handle so a repeated sound loads once.
    /// </summary>
    /// <remarks>
    /// The prefix is a constructor argument rather than a constant because AddressablesSetup
    /// registers two separate audio groups: SoundEffects under <c>sfx/</c> and Voice under
    /// <c>vo/</c>. A player built for one silently finds nothing in the other — a missing address
    /// is a warning, not an exception, so the wrong prefix presents as "the audio just doesn't
    /// play" with a correct-looking call site.
    /// </remarks>
    internal sealed class AddressableSoundEffectPlayer
    {
        public const string CardSoundPrefix = "sfx/";
        public const string HeroVoicePrefix = "vo/";

        private readonly string _addressPrefix;
        private readonly string _owner;
        private readonly bool _logMissing;
        private readonly AudioSource _source;
        private readonly Dictionary<string, AsyncOperationHandle<AudioClip>> _handles = new();
        private readonly Dictionary<string, List<string>> _variants = new();

        /// <summary>Highest variant suffix looked for: <c>base</c>, then <c>_2</c> up to this.</summary>
        /// <remarks>A bound, not a budget. Existence is one Addressables lookup per suffix and the
        /// probe runs once per sound for the life of the player, so the cost is small and fixed —
        /// but it IS per suffix, so this should not grow to a number nobody would ever author.</remarks>
        public const int MaxVariants = 6;

        /// <param name="logMissing">Whether an unresolved key warns. A caller whose content is
        /// KNOWN to be partial — hero voice, where most classes are unrecorded — passes false, so
        /// silence stays silent instead of filling the console every turn. Everything else should
        /// leave it true: a missing address is otherwise completely invisible.</param>
        public AddressableSoundEffectPlayer(
            AudioSource source, string addressPrefix = CardSoundPrefix, string owner = null,
            bool logMissing = true)
        {
            _source = source;
            _addressPrefix = addressPrefix ?? CardSoundPrefix;
            _owner = owner ?? nameof(CardSoundDirector);
            _logMissing = logMissing;
        }

        /// <summary>
        /// Plays <paramref name="soundId"/>, choosing at random among its promoted variants.
        /// </summary>
        /// <remarks>
        /// <para>A shared library sound is not rare: <c>undead_summon</c> covers 22 cards, so one
        /// clip is heard several times a turn and stops reading as the game and starts reading as a
        /// loop. Variants are the fix, and they cost nothing at the call site — the router still
        /// emits one id and knows nothing about how many clips back it.</para>
        ///
        /// <para><b>The set is discovered, not declared.</b> The first play of a sound probes
        /// <c>&lt;id&gt;_2</c> … <c>_<see cref="MaxVariants"/></c> for an actual Addressables entry
        /// and keeps whichever exist. Nothing lists them, so promoting a second take is the whole
        /// act of adding one — no table to edit here, which is the table that would otherwise be
        /// forgotten and leave a promoted clip that never plays.</para>
        ///
        /// <para>A gap is tolerated on purpose: <c>_3</c> present without <c>_2</c> still counts.
        /// The alternative — stopping at the first miss — turns one deleted file into the silent
        /// loss of every variant above it.</para>
        /// </remarks>
        public void Play(string soundId)
        {
            if (string.IsNullOrEmpty(soundId) || _source == null) return;
            string address = _addressPrefix + soundId;
            if (!_variants.TryGetValue(address, out List<string> variants))
            {
                // The base address is assumed to exist and is played immediately; the probes below
                // land later and only ever widen the choice. A first play therefore never waits on
                // them, which matters because the first play of a sound is usually mid-animation.
                variants = new List<string> { address };
                _variants[address] = variants;
                ProbeVariants(address, variants);
            }

            PlayAddress(variants.Count == 1 ? address : variants[Random.Range(0, variants.Count)]);
        }

        private void ProbeVariants(string address, List<string> variants)
        {
            for (int i = 2; i <= MaxVariants; i++)
            {
                string candidate = address + "_" + i;
                AsyncOperationHandle<IList<IResourceLocation>> probe =
                    Addressables.LoadResourceLocationsAsync(candidate);
                probe.Completed += handle =>
                {
                    // A missing key resolves to an EMPTY list rather than failing, which is what
                    // makes this usable as an existence check at all. Do not "fix" it into a
                    // LoadAssetAsync with a caught exception: that logs, and the log is the thing
                    // a bounded probe exists to avoid.
                    if (handle.Status == AsyncOperationStatus.Succeeded &&
                        handle.Result != null && handle.Result.Count > 0 &&
                        !variants.Contains(candidate))
                        variants.Add(candidate);
                    if (handle.IsValid()) Addressables.Release(handle);
                };
            }
        }

        private void PlayAddress(string address)
        {
            if (_handles.TryGetValue(address, out AsyncOperationHandle<AudioClip> cached))
            {
                if (cached.IsDone) PlayLoaded(cached, address);
                else cached.Completed += handle => PlayLoaded(handle, address);
                return;
            }

            AsyncOperationHandle<AudioClip> load = Addressables.LoadAssetAsync<AudioClip>(address);
            _handles[address] = load;
            load.Completed += handle => PlayLoaded(handle, address);
        }

        /// <summary>Start loading <paramref name="soundId"/>'s base clip without playing it, so a
        /// later <see cref="Play"/> lands on the frame it is called rather than after a load. For a
        /// sound tied to a beat of an animation that has a lead-in, like the seal's drop.</summary>
        public void Preload(string soundId)
        {
            if (string.IsNullOrEmpty(soundId)) return;
            string address = _addressPrefix + soundId;
            if (_handles.ContainsKey(address)) return;
            _handles[address] = Addressables.LoadAssetAsync<AudioClip>(address);
        }

        public void ReleaseAll()
        {
            foreach (AsyncOperationHandle<AudioClip> handle in _handles.Values)
                if (handle.IsValid()) Addressables.Release(handle);
            _handles.Clear();
            _variants.Clear();
        }

        private void PlayLoaded(AsyncOperationHandle<AudioClip> handle, string address)
        {
            if (_source == null) return;
            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                _source.PlayOneShot(handle.Result);
            else if (_logMissing)
                Debug.LogWarning($"[{_owner}] no AudioClip at Addressables key '{address}'.");
        }
    }
}
