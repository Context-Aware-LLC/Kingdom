using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Background music: loads every clip under the Addressables "music" label and loops through
    /// them in shuffled order, forever. Sam, 2026-09-05: "play them all at random in a loop" — six
    /// ambient tracks under <c>Assets/Sound/Music/</c>, registered by <c>AddressablesSetup.Run()</c>
    /// into the Music group at <c>music/&lt;fileName&gt;</c>, all carrying the "music" label.
    ///
    /// <para>Self-bootstraps once per process via <see cref="RuntimeInitializeOnLoadMethodAttribute"/>
    /// rather than being wired into a specific scene's bootstrap, unlike CardSoundDirector/
    /// HeroVoiceDirector: music has no match-specific state to bind to and should keep playing
    /// across a menu-to-match transition, which <c>DontDestroyOnLoad</c> gives it for free.</para>
    /// </summary>
    public sealed class MusicDirector : MonoBehaviour
    {
        private const string MusicLabel = "music";

        private readonly List<AudioClip> _clips = new();
        private readonly List<int> _order = new();
        private int _cursor;
        private AudioSource _source;
        private AsyncOperationHandle<IList<AudioClip>> _handle;
        private bool _handleValid;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // HeadlessVerify and CI batchmode runs never need music, and batchmode Addressables/
            // audio timing is untested territory this director has no reason to enter.
            if (Application.isBatchMode) return;

            var go = new GameObject("~MusicDirector");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<MusicDirector>();
        }

        private void Awake()
        {
            // TryGetComponent, not `??` — see CardSoundDirector.Init for why the null-coalescing
            // operator is the wrong null check against a UnityEngine.Object.
            if (!TryGetComponent(out AudioSource source)) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = AudioSettings.MusicGroup;
            _source = source;

            _handle = Addressables.LoadAssetsAsync<AudioClip>(MusicLabel, null);
            _handleValid = true;
            _handle.Completed += OnClipsLoaded;
        }

        private void OnClipsLoaded(AsyncOperationHandle<IList<AudioClip>> handle)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null || handle.Result.Count == 0)
            {
                Debug.LogWarning($"[MusicDirector] no clips resolved under the '{MusicLabel}' label.");
                return;
            }
            _clips.AddRange(handle.Result);
            Reshuffle();
            PlayNext();
        }

        private void Update()
        {
            if (_clips.Count == 0 || _source == null) return;
            if (!_source.isPlaying) PlayNext();
        }

        private void PlayNext()
        {
            if (_cursor >= _order.Count) Reshuffle();
            _source.clip = _clips[_order[_cursor]];
            _source.Play();
            _cursor++;
        }

        // Fisher-Yates, re-run whenever the cursor exhausts the current pass — the loop never runs
        // dry and never repeats the same fixed order twice in a row.
        private void Reshuffle()
        {
            _order.Clear();
            for (int i = 0; i < _clips.Count; i++) _order.Add(i);
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            _cursor = 0;
        }

        private void OnDestroy()
        {
            if (_handleValid && _handle.IsValid()) Addressables.Release(_handle);
        }
    }
}
