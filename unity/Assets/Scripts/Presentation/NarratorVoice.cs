using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Kingdom.Presentation
{
    /// <summary>One replaceable narration line, owned by the presentation displaying it.
    /// Stopping also cancels an unfinished load so a dismissed lesson can never speak later.
    ///
    /// <para><b>The Herald never talks over himself</b> (2026-11-05). Every instance is the same
    /// voice, so a line that starts stops whichever line is already speaking. The opening line
    /// (<see cref="OpeningCinematic"/>) is spoken over the first screens, and a tidbit that opens
    /// under it takes the floor.</para></summary>
    public sealed class NarratorVoice : MonoBehaviour
    {
        private static NarratorVoice _speaking;

        private AudioSource _source;
        private AsyncOperationHandle<AudioClip> _load;
        private bool _closed;
        private bool _closeWhenDone;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _speaking = null;

        public static NarratorVoice Create()
        {
            var host = new GameObject("~NarratorVoice");
            return host.AddComponent<NarratorVoice>();
        }

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;
            _source.outputAudioMixerGroup = AudioSettings.SfxGroup;
        }

        /// <param name="closeWhenDone">For a line nobody owns: the voice destroys itself when the
        /// line ends, fails to load, or is cut off by another line.</param>
        public void Play(string address, bool closeWhenDone = false)
        {
            Stop();
            if (_closed) return;
            _closeWhenDone = closeWhenDone;
            if (string.IsNullOrEmpty(address)) { Finish(); return; }
            StartCoroutine(LoadAndPlay(address));
        }

        private IEnumerator LoadAndPlay(string address)
        {
            _load = Addressables.LoadAssetAsync<AudioClip>(address);
            yield return _load;
            if (_load.Status != AsyncOperationStatus.Succeeded || _load.Result == null)
            {
                Debug.LogWarning($"[NarratorVoice] Could not load '{address}'; the text remains available.");
                Finish();
                yield break;
            }

            if (_speaking != null && _speaking != this) _speaking.Interrupt();
            _speaking = this;
            _source.clip = _load.Result;
            _source.Play();

            if (!_closeWhenDone) yield break;
            while (_source.isPlaying) yield return null;
            Finish();
        }

        /// <summary>Another line took the floor.</summary>
        private void Interrupt()
        {
            Stop();
            Finish();
        }

        private void Finish()
        {
            if (_closeWhenDone) Close();
        }

        public void Stop()
        {
            if (_speaking == this) _speaking = null;
            StopAllCoroutines();
            if (_source != null)
            {
                _source.Stop();
                _source.clip = null;
            }
            if (_load.IsValid()) Addressables.Release(_load);
            _load = default;
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            Stop();
            gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
        }

        private void OnDisable() => Stop();
        private void OnDestroy() => Stop();
    }
}
