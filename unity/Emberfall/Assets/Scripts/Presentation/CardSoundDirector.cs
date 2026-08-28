using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Emberfall.Presentation
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

        public void Play(string soundId)
        {
            if (string.IsNullOrEmpty(soundId) || _source == null) return;
            string address = _addressPrefix + soundId;
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

        public void ReleaseAll()
        {
            foreach (AsyncOperationHandle<AudioClip> handle in _handles.Values)
                if (handle.IsValid()) Addressables.Release(handle);
            _handles.Clear();
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
