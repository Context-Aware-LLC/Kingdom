using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Kingdom.Presentation
{
    /// <summary>One reveal's Herald cues. Its owner stops it on dismissal, replacement or detach,
    /// including while Addressables is still loading. Batch fanfares play in sequence.</summary>
    internal sealed class PackRevealAudio : MonoBehaviour
    {
        private static readonly string[] Trumpets =
        {
            "sfx/pack_legendary_trumpet", "sfx/pack_legendary_trumpet_2",
        };

        private AudioSource _source;
        private readonly AsyncOperationHandle<AudioClip>[] _trumpets =
            new AsyncOperationHandle<AudioClip>[Trumpets.Length];
        private int _pending;
        private bool _playing;
        private bool _stopped;

        public static PackRevealAudio Create()
        {
            if (!Application.isPlaying) return null;
            var host = new GameObject("PackRevealAudio");
            var audio = host.AddComponent<PackRevealAudio>();
            audio._source = host.AddComponent<AudioSource>();
            audio._source.playOnAwake = false;
            audio._source.loop = false;
            audio._source.spatialBlend = 0f;
            audio._source.outputAudioMixerGroup = AudioSettings.SfxGroup;
            for (int i = 0; i < Trumpets.Length; i++)
                audio._trumpets[i] = Addressables.LoadAssetAsync<AudioClip>(Trumpets[i]);
            return audio;
        }

        public void LegendaryRevealed()
        {
            if (_stopped) return;
            _pending++;
            if (_playing) return;
            _playing = true;
            StartCoroutine(PlayPending());
        }

        private IEnumerator PlayPending()
        {
            while (_pending > 0)
            {
                _pending--;
                int variant = Random.Range(0, _trumpets.Length);
                yield return PlayClip(_trumpets[variant], Trumpets[variant]);
            }
            _playing = false;
        }

        private IEnumerator PlayClip(AsyncOperationHandle<AudioClip> load, string address)
        {
            if (!load.IsDone) yield return load;
            if (load.Status != AsyncOperationStatus.Succeeded || load.Result == null)
            {
                Debug.LogWarning($"[PackRevealAudio] no AudioClip at '{address}'.");
                yield break;
            }
            _source.clip = load.Result;
            _source.Play();
            yield return new WaitForSecondsRealtime(load.Result.length);
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _pending = 0;
            StopAllCoroutines();
            if (_source != null)
            {
                _source.Stop();
                _source.clip = null;
            }
            foreach (AsyncOperationHandle<AudioClip> load in _trumpets)
                if (load.IsValid()) Addressables.Release(load);
        }

        private void OnDestroy() => Stop();
    }
}
