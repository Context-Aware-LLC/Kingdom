using System.Collections;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The two heroes trade a line as the versus screen comes up (whimsy pass #3, #422): the pair's
    /// opener speaks, then the other hero answers, each with a caption, since phones mostly play
    /// muted. It outlives the versus screen on purpose and finishes over the mulligan.
    /// </summary>
    /// <remarks>
    /// <para>Only the two PRIMARY classes speak, and both are public on the snapshot, so nothing
    /// here reveals a hidden secondary.</para>
    ///
    /// <para>The order is the pair's, not the seats': the hero in
    /// <see cref="VersusBanterLines.Openers"/> always goes first. Lines and captions come from
    /// <see cref="VersusBanterLines"/>, which <c>tooling/sfx/gen_versus_banter.py</c> writes from
    /// <c>voice-lines.json</c>; clips are <c>vo/vo_&lt;speaker&gt;_vs_&lt;other&gt;_1</c>, plus any
    /// promoted <c>_2</c>…<c>_6</c> alternates.</para>
    ///
    /// <para>A clip that does not resolve still shows its caption for <see cref="FallbackHold"/>,
    /// so a missing address reads as a silent line, not a missing one.</para>
    /// </remarks>
    public sealed class VersusBanter : MonoBehaviour
    {
        private const string Prefix = AddressableSoundEffectPlayer.HeroVoicePrefix;
        private const int MaxVariants = AddressableSoundEffectPlayer.MaxVariants;

        /// <summary>After the versus screen's 0.5s reveal, so the first line lands on a face.</summary>
        private const float StartDelay = 0.6f;
        private const float GapBetweenLines = 0.25f;
        private const float CaptionTail = 0.5f;     // caption stays this long after its clip ends
        private const float FallbackHold = 1.8f;    // caption time for a line with no clip
        private const float CaptionFade = 0.2f;

        // Below the versus screen's status line, clear of the medallions and nameplates.
        private const float CaptionYFrac = -0.43f;
        private const float CaptionSizeFrac = 0.26f;   // TMP point value, x10 the em fraction (see VersusOverlay)
        private const int SortOrder = 450;             // above VersusOverlay's 400 band

        private static readonly Color CaptionTint = new Color(0.96f, 0.93f, 0.86f);

        private AudioSource _source;
        private TextMeshPro _caption;
        private readonly List<AsyncOperationHandle<AudioClip>> _handles = new();

        public static VersusBanter Create(Camera cam, CardClass localClass, CardClass remoteClass)
        {
            if (cam == null || localClass == CardClass.NEUTRAL || remoteClass == CardClass.NEUTRAL)
                return null;
            if (!VersusBanterLines.Lines.ContainsKey((localClass, remoteClass))) return null;

            var go = new GameObject("VersusBanter");
            go.transform.SetParent(cam.transform, false);
            float dist = cam.nearClipPlane + 1f;
            go.transform.localPosition = new Vector3(0f, 0f, dist);
            var banter = go.AddComponent<VersusBanter>();
            banter.Build(cam, dist);

            bool localOpens = localClass == remoteClass
                || VersusBanterLines.Openers.Contains((localClass, remoteClass));
            CardClass first = localOpens ? localClass : remoteClass;
            CardClass second = localOpens ? remoteClass : localClass;
            banter.StartCoroutine(banter.Run(first, second));
            return banter;
        }

        private void Build(Camera cam, float dist)
        {
            float h = cam.orthographic
                ? cam.orthographicSize * 2f
                : 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;
            _source.outputAudioMixerGroup = AudioSettings.SfxGroup;

            var cgo = new GameObject("Caption");
            cgo.transform.SetParent(transform, false);
            cgo.transform.localPosition = new Vector3(0f, CaptionYFrac * h, -0.03f);
            _caption = cgo.AddComponent<TextMeshPro>();
            _caption.rectTransform.sizeDelta = new Vector2(1.6f * h, 0.12f * h);
            _caption.fontSize = CaptionSizeFrac * h;
            BoardController.ApplyUiFont(_caption);
            _caption.alignment = TextAlignmentOptions.Center;
            _caption.textWrappingMode = TextWrappingModes.Normal;
            _caption.sortingOrder = SortOrder;
            _caption.color = new Color(CaptionTint.r, CaptionTint.g, CaptionTint.b, 0f);
            var mat = _caption.fontMaterial;
            mat.EnableKeyword("OUTLINE_ON");
            mat.SetColor(Shader.PropertyToID("_OutlineColor"), new Color(0f, 0f, 0f, 0.9f));
            mat.SetFloat(Shader.PropertyToID("_OutlineWidth"), 0.18f);
        }

        private IEnumerator Run(CardClass first, CardClass second)
        {
            AudioClip firstClip = null, secondClip = null;
            Coroutine loadFirst = StartCoroutine(Load(Stem(first, second), c => firstClip = c));
            Coroutine loadSecond = StartCoroutine(Load(Stem(second, first), c => secondClip = c));
            yield return new WaitForSeconds(StartDelay);
            yield return loadFirst;
            yield return Speak(first, second, firstClip);
            yield return new WaitForSeconds(GapBetweenLines);
            yield return loadSecond;
            yield return Speak(second, first, secondClip);
            Destroy(gameObject);
        }

        private IEnumerator Speak(CardClass speaker, CardClass other, AudioClip clip)
        {
            // Authored strings only, but the hero name is set bold, so rich text stays on.
            _caption.text = $"<b>{VersusBanterLines.HeroName(speaker)}</b>  {VersusBanterLines.Lines[(speaker, other)]}";
            yield return FadeCaption(0f, 1f);
            float hold;
            if (clip != null)
            {
                _source.PlayOneShot(clip);
                hold = clip.length + CaptionTail;
            }
            else
            {
                hold = FallbackHold;
            }
            yield return new WaitForSeconds(Mathf.Max(0f, hold - CaptionFade));
            yield return FadeCaption(1f, 0f);
        }

        private IEnumerator FadeCaption(float from, float to)
        {
            for (float t = 0f; t < CaptionFade; t += Time.deltaTime)
            {
                SetCaptionAlpha(Mathf.Lerp(from, to, t / CaptionFade));
                yield return null;
            }
            SetCaptionAlpha(to);
        }

        private void SetCaptionAlpha(float a)
        {
            Color c = _caption.color;
            c.a = a;
            _caption.color = c;
        }

        /// <summary>Picks at random among the base clip and whichever alternates were promoted,
        /// the same discovery rule as <see cref="AddressableSoundEffectPlayer.Play"/>.</summary>
        private IEnumerator Load(string stem, System.Action<AudioClip> done)
        {
            var candidates = new List<string> { Prefix + stem };
            for (int i = 2; i <= MaxVariants; i++)
            {
                string candidate = Prefix + stem + "_" + i;
                AsyncOperationHandle<IList<IResourceLocation>> probe =
                    Addressables.LoadResourceLocationsAsync(candidate);
                yield return probe;
                if (probe.Status == AsyncOperationStatus.Succeeded && probe.Result != null && probe.Result.Count > 0)
                    candidates.Add(candidate);
                if (probe.IsValid()) Addressables.Release(probe);
            }

            string address = candidates[Random.Range(0, candidates.Count)];
            AsyncOperationHandle<AudioClip> load = Addressables.LoadAssetAsync<AudioClip>(address);
            _handles.Add(load);
            yield return load;
            if (load.Status == AsyncOperationStatus.Succeeded && load.Result != null)
                done(load.Result);
            else
                Debug.LogWarning($"[{nameof(VersusBanter)}] no AudioClip at Addressables key '{address}'.");
        }

        private static string Stem(CardClass speaker, CardClass other) =>
            $"vo_{speaker.ToString().ToLowerInvariant()}_vs_{other.ToString().ToLowerInvariant()}_1";

        private void OnDestroy()
        {
            foreach (AsyncOperationHandle<AudioClip> handle in _handles)
                if (handle.IsValid()) Addressables.Release(handle);
            _handles.Clear();
        }
    }
}
