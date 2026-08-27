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
            AudioSource source = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
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

    internal sealed class AddressableSoundEffectPlayer
    {
        private const string AddressPrefix = "sfx/";
        private readonly AudioSource _source;
        private readonly Dictionary<string, AsyncOperationHandle<AudioClip>> _handles = new();

        public AddressableSoundEffectPlayer(AudioSource source) => _source = source;

        public void Play(string soundId)
        {
            if (string.IsNullOrEmpty(soundId) || _source == null) return;
            string address = AddressPrefix + soundId;
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
            else
                Debug.LogWarning($"[CardSoundDirector] no AudioClip at Addressables key '{address}'.");
        }
    }
}
