using System.Collections;
using Kingdom.Core.Contracts;
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
        private AudioSource _foley;
        private AudioSource _chimeSource;
        private AsyncOperationHandle<AudioClip> _paper;
        private AsyncOperationHandle<AudioClip> _chime;
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
            audio._foley = host.AddComponent<AudioSource>();
            audio._foley.playOnAwake = false;
            audio._foley.spatialBlend = 0f;
            audio._foley.outputAudioMixerGroup = AudioSettings.SfxGroup;
            audio._chimeSource = host.AddComponent<AudioSource>();
            audio._chimeSource.playOnAwake = false;
            audio._chimeSource.spatialBlend = 0f;
            audio._chimeSource.outputAudioMixerGroup = AudioSettings.SfxGroup;
            audio._paper = Addressables.LoadAssetAsync<AudioClip>("sfx/paper_mulligan_toss");
            audio._chime = Addressables.LoadAssetAsync<AudioClip>("sfx/mana_orb_tap");
            for (int i = 0; i < Trumpets.Length; i++)
                audio._trumpets[i] = Addressables.LoadAssetAsync<AudioClip>(Trumpets[i]);
            return audio;
        }

        public void CardArrived(int index)
            => PlayReady(_foley, _paper, 0.88f + Mathf.Min(index, 9) * 0.025f, 0.22f);

        public void CardRevealed(Rarity rarity)
        {
            PlayReady(_foley, _paper, 1.12f, 0.35f);
            if (rarity >= Rarity.RARE)
                PlayReady(_chimeSource, _chime, rarity == Rarity.EPIC ? 1.25f : 1f, 0.42f);
        }

        // Do not play a stale whoosh when an asynchronous load finally finishes. Only the
        // deliberate Legendary fanfare queues; tactile sounds must belong to the current beat.
        private void PlayReady(AudioSource source, AsyncOperationHandle<AudioClip> load,
            float pitch, float volume)
        {
            if (_stopped || !load.IsValid() || !load.IsDone ||
                load.Status != AsyncOperationStatus.Succeeded || load.Result == null) return;
            source.pitch = pitch;
            source.PlayOneShot(load.Result, volume);
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
            if (_foley != null) _foley.Stop();
            if (_chimeSource != null) _chimeSource.Stop();
            if (_paper.IsValid()) Addressables.Release(_paper);
            if (_chime.IsValid()) Addressables.Release(_chime);
            foreach (AsyncOperationHandle<AudioClip> load in _trumpets)
                if (load.IsValid()) Addressables.Release(load);
        }

        private void OnDestroy() => Stop();
    }
}
