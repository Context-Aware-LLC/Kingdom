using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Kingdom.Presentation
{
    /// <summary>Private table fidgets. The input reference is only a modal/intro guard; these
    /// props have no command sender and never subscribe to or publish a match event.</summary>
    internal sealed class RoyalCouncilTableToys : MonoBehaviour
    {
        internal const string CrownVoiceAddress = "vo/vo_herald_crown";
        internal const string OrbSoundAddress = "sfx/mana_orb_tap";
        private readonly TablePropRules _rules = new TablePropRules();
        private readonly System.Random _random = new System.Random();
        private readonly List<AsyncOperationHandle> _loads = new List<AsyncOperationHandle>();
        private readonly AudioSource[] _orbs = new AudioSource[20];
        private InputRouter _input;
        private AudioSource _herald;
        private AudioClip _orbClip, _crownClip;
        private Transform _box, _note, _receipt;
        private Material _wood, _brass, _paper, _ink;
        private bool _filing;

        public void Bind(InputRouter input) => _input = input;
        private bool CanTouch => _input != null && !_input.BlocksInput;

        public void Initialize(RoyalCouncilBoard board)
        {
            Transform crown = board.Part("RC.Static.Brand.CrownFuse.Icon");
            if (crown != null)
            {
                Bounds bounds = crown.GetComponent<Renderer>().bounds;
                Transform pivot = Hit("Crown touch", bounds, () =>
                {
                    if (!CanTouch) return;
                    if (_rules.TapCrown(Time.unscaledTimeAsDouble) && _crownClip != null)
                        _herald.PlayOneShot(_crownClip);
                });
                // The imported mesh uses a table-centred origin. A centred wrapper keeps a
                // touch target on the crown without moving or reauthoring the far-rail emblem.
                pivot.gameObject.AddComponent<LocalPropWobble>().Initialize(crown);
            }

            for (int seat = 0; seat < 2; seat++)
            for (int i = 0; i < 10; i++)
            {
                int index = seat * 10 + i;
                Transform orb = board.Part($"RC.P{seat}.Mana.Crystal.{i:00}");
                if (orb == null) continue;
                Hit($"Mana orb {index + 1} touch", orb.GetComponent<Renderer>().bounds, () =>
                {
                    if (CanTouch && _orbClip != null) _orbs[index].PlayOneShot(_orbClip);
                });
                if (Application.isPlaying)
                {
                    _orbs[index] = Source($"Glass note {index + 1}");
                    _orbs[index].pitch = TablePropRules.OrbPitch(index);
                }
            }
            Transform glass = board.Part("RC.Prop.Hourglass.Glass");
            if (glass != null)
                Hit("Hourglass touch", glass.GetComponent<Renderer>().bounds, () =>
                {
                    if (CanTouch) board.Clock.Wobble();
                });

            BuildSuggestionBox();
            if (Application.isPlaying)
            {
                _herald = Source("Offstage Herald");
                StartCoroutine(LoadOptional(CrownVoiceAddress, clip => _crownClip = clip));
                StartCoroutine(LoadOptional(OrbSoundAddress, clip => _orbClip = clip));
            }
        }

        private Transform Hit(string name, Bounds bounds, Action press)
        {
            var hit = new GameObject(name);
            hit.transform.SetParent(transform, false);
            hit.transform.position = bounds.center;
            hit.AddComponent<BoxCollider>().size = bounds.size + new Vector3(0.06f, 0.06f, 0.06f);
            hit.AddComponent<LocalPropPress>().Pressed = () =>
            {
                if (!CanTouch) return;
                press();
                hit.GetComponent<LocalPropWobble>()?.Tap();
            };
            return hit.transform;
        }

        private AudioSource Source(string name)
        {
            var host = new GameObject(name);
            host.transform.SetParent(transform, false);
            var source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = AudioSettings.SfxGroup;
            return source;
        }

        private IEnumerator LoadOptional(string address, Action<AudioClip> ready)
        {
            // The Herald recording awaits casting. Probe once, silently, and never queue a
            // tap to surprise the player after an asynchronous load or after leaving the table.
            var probe = Addressables.LoadResourceLocationsAsync(address, typeof(AudioClip));
            _loads.Add(probe);
            yield return probe;
            bool exists = probe.Status == AsyncOperationStatus.Succeeded && probe.Result != null && probe.Result.Count > 0;
            _loads.Remove(probe);
            Addressables.Release(probe);
            if (!exists) yield break;
            var load = Addressables.LoadAssetAsync<AudioClip>(address);
            _loads.Add(load);
            yield return load;
            if (load.Status == AsyncOperationStatus.Succeeded) ready(load.Result);
        }

        private void BuildSuggestionBox()
        {
            _wood = TabletopToyGeometry.Material("Suggestion box walnut", new Color(0.20f, 0.085f, 0.035f));
            _brass = TabletopToyGeometry.Material("Suggestion box brass", new Color(0.62f, 0.39f, 0.10f), 0.7f);
            _paper = TabletopToyGeometry.Material("Suggestion paper", new Color(0.88f, 0.79f, 0.59f));
            _ink = TabletopToyGeometry.Material("Suggestion slot", new Color(0.025f, 0.017f, 0.013f));
            _box = new GameObject("Suggestion box").transform;
            _box.SetParent(transform, false);
            // On the near rail below our deck, resting on the rail strip (z -0.163). Beside the
            // stamper it sat behind it on screen, and the stamper's touch box took nearly every tap
            // (Sam, 2026-10-20). Nudged 0.15 left of flush with the deck slot (x 9.95) the same day.
            _box.position = new Vector3(9.11f, -5.21f, -0.42f);
            Box(_box, "Walnut case", Vector3.zero, new Vector3(1.3f, 1.15f, 0.55f), _wood);
            Box(_box, "Brass lid", new Vector3(0f, 0f, -0.29f), new Vector3(1.38f, 1.22f, 0.06f), _brass);
            Box(_box, "Dark letter slot", new Vector3(0f, 0.20f, -0.327f), new Vector3(0.90f, 0.10f, 0.025f), _ink);
            // Larger than it first shipped (1.1, Sam 2026-10-20); auto-size keeps a longer word in
            // another language on the lid instead of wrapping it.
            TextMeshPro plate = TabletopToyGeometry.Label(_box, Loc.Get("board_suggestion_box_label"),
                new Vector3(0f, -0.24f, -0.335f), new Vector2(1.26f, 0.4f), 1.45f);
            plate.enableAutoSizing = true;
            plate.fontSizeMin = 1.0f;
            plate.fontSizeMax = 1.45f;
            _box.gameObject.AddComponent<BoxCollider>().size = new Vector3(1.4f, 1.25f, 0.7f);
            _box.gameObject.AddComponent<LocalPropPress>().Pressed = () =>
            {
                if (CanTouch && !_filing) StartCoroutine(FileSuggestion());
            };
            _note = new GameObject("Folded suggestion").transform;
            _note.SetParent(_box, false);
            Box(_note, "Folded paper", Vector3.zero, new Vector3(0.66f, 0.44f, 0.035f), _paper);
            Box(_note, "Fold crease", new Vector3(0f, 0f, -0.021f), new Vector3(0.008f, 0.42f, 0.003f), _brass);
            _note.gameObject.SetActive(false);
            _receipt = new GameObject("Filed receipt").transform;
            _receipt.SetParent(_box, false);
            Box(_receipt, "Receipt slip", Vector3.zero, new Vector3(3.0f, 1.05f, 0.035f), _paper);
            TabletopToyGeometry.Label(_receipt, Loc.Get("board_suggestion_box_receipt"),
                new Vector3(0f, 0f, -0.026f), new Vector2(2.8f, 0.9f), 1.65f);
            _receipt.gameObject.SetActive(false);
        }

        private IEnumerator FileSuggestion()
        {
            _filing = true;
            _note.gameObject.SetActive(true);
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.55f)
            {
                _note.localPosition = Vector3.Lerp(new Vector3(0f, 0.85f, -0.85f), new Vector3(0f, 0.20f, -0.28f), t);
                _note.localRotation = Quaternion.Euler(Mathf.Lerp(0f, 75f, t), 0f, -10f * (1f - t));
                _note.localScale = Vector3.one * (1f - 0.8f * t);
                yield return null;
            }
            _note.gameObject.SetActive(false);
            if (TablePropRules.ReturnsSuggestionReceipt(_random.Next(20)))
            {
                yield return new WaitForSecondsRealtime(0.3f);
                _receipt.gameObject.SetActive(true);
                for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.45f)
                {
                    _receipt.localPosition = Vector3.Lerp(new Vector3(0f, 0.20f, -0.37f), new Vector3(-1.9f, 0.35f, -0.50f), t);
                    _receipt.localScale = Vector3.one * Mathf.Lerp(0.08f, 1f, t);
                    yield return null;
                }
                _receipt.localScale = Vector3.one;
                yield return new WaitForSecondsRealtime(3f);
                _receipt.gameObject.SetActive(false);
            }
            _filing = false;
        }

        private static void Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
            => TabletopToyGeometry.Solid(parent, name, PrimitiveType.Cube, position, size, material);

        private void OnDisable()
        {
            StopAllCoroutines();
            _filing = false;
            if (_note != null) _note.gameObject.SetActive(false);
            if (_receipt != null) _receipt.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            foreach (var load in _loads) if (load.IsValid()) Addressables.Release(load);
            TabletopToyGeometry.Destroy(_wood);
            TabletopToyGeometry.Destroy(_brass);
            TabletopToyGeometry.Destroy(_paper);
            TabletopToyGeometry.Destroy(_ink);
        }
    }

    internal sealed class LocalPropPress : MonoBehaviour, IPointerPressTarget
    {
        public Action Pressed;
        void IPointerPressTarget.OnPointerDown() => Pressed?.Invoke();
    }

    internal sealed class LocalPropWobble : MonoBehaviour
    {
        private Quaternion _rest;
        private float _tapAt = -10f;
        public void Initialize(Transform visual)
        {
            _rest = transform.rotation;
            visual.SetParent(transform, true);
        }
        public void Tap() => _tapAt = Time.unscaledTime;
        private void Update()
        {
            float t = Mathf.Clamp01((Time.unscaledTime - _tapAt) / 0.4f);
            transform.rotation = _rest * Quaternion.Euler(0f, 0f, Mathf.Sin(t * Mathf.PI * 4f) * (1f - t) * 3f);
        }
        private void OnDisable() => transform.rotation = _rest;
    }
}
