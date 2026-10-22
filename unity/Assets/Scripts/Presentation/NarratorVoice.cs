using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Kingdom.Presentation
{
    /// <summary>One replaceable narration line, owned by the presentation displaying it.
    /// Stopping also cancels an unfinished load so a dismissed lesson can never speak later.</summary>
    public sealed class NarratorVoice : MonoBehaviour
    {
        private AudioSource _source;
        private AsyncOperationHandle<AudioClip> _load;
        private bool _closed;

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

        public void Play(string address)
        {
            Stop();
            if (_closed || string.IsNullOrEmpty(address)) return;
            StartCoroutine(LoadAndPlay(address));
        }

        private IEnumerator LoadAndPlay(string address)
        {
            _load = Addressables.LoadAssetAsync<AudioClip>(address);
            yield return _load;
            if (_load.Status != AsyncOperationStatus.Succeeded || _load.Result == null)
            {
                Debug.LogWarning($"[NarratorVoice] Could not load '{address}'; the text remains available.");
                yield break;
            }
            _source.clip = _load.Result;
            _source.Play();
        }

        public void Stop()
        {
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
