using System.Collections;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>The stamper ends the local turn. A server timeout leaves a tagged receipt;
    /// an opponent-turn touch only rattles the latched prop.</summary>
    public sealed class RoyalCouncilSeal : MonoBehaviour, IPointerPressTarget
    {
        // Stamper, wax and both supports share the hourglass perspective, mirrored inward.
        public static Quaternion RestRotation => Quaternion.Euler(RoyalCouncilBoard.PropTilt, 6f, 0f);
        private Transform _wax;
        private Transform _stamp;
        private Renderer _renderer;
        private Renderer _stampRenderer;
        private MaterialPropertyBlock _properties;
        private Vector3 _rest;
        private Vector3 _scale;
        private Vector3 _stampRest;
        private Quaternion _stampRotation;
        private Quaternion _stampPressRotation;
        private InputRouter _input;
        private PlayerId _local;
        private bool _ready, _pressing, _opponentTurn, _automatic, _rattling, _hasAuthority;
        private bool _presentedOpponentTurn;
        private int _authorityTurn;
        private readonly TurnSealRecord _record = new TurnSealRecord();
        private GameObject _tag, _latch;
        private Material _paperMaterial, _brassMaterial;
        private AudioSource _rattleAudio;
        private AudioClip _rattleClip;
        public bool IsReady => _ready && !_pressing && _input != null && !_input.BlocksInput;

        public void Initialize(Transform wax, Transform stamper)
        {
            _wax = wax; _rest = wax.localPosition; _scale = wax.localScale;
            _stamp = stamper; _stampRest = stamper.position; _stampRotation = stamper.rotation;
            _stampPressRotation = Quaternion.FromToRotation(stamper.up, wax.up) * _stampRotation;
            _renderer = wax.GetComponent<Renderer>();
            _stampRenderer = stamper.GetComponent<Renderer>();
            _properties = new MaterialPropertyBlock();
            Bounds b = _stampRenderer.bounds;
            transform.position = b.center;
            var hit = gameObject.AddComponent<BoxCollider>();
            hit.size = new Vector3(Mathf.Max(1.1f, b.size.x * 1.2f), Mathf.Max(1.0f, b.size.y * 1.2f), b.size.z + 0.12f);
            UiSfx.Preload(UiSfx.WaxStamp);
            BuildReceiptAndLatch();
            Show(false);
        }

        public void Bind(PlayerId local, InputRouter input) { _local = local; _input = input; }
        public void OnStateUpdated(GameState state)
        {
            // Local playtests have no separate truth subscription. Network play always uses
            // OnAuthorityStateUpdated for input, even while older commands are being presented.
            if (!_hasAuthority) SetAuthority(state);
            ulong revision = _record.Revision;
            _record.OnStateUpdated(state);
            _presentedOpponentTurn = state != null && state.ActivePlayer != _local
                && state.Phase == TurnPhase.Main && state.Outcome == MatchOutcome.InProgress;
            if ((_automatic && revision != _record.Revision)
                || state == null || state.Outcome != MatchOutcome.InProgress || state.Phase != TurnPhase.Main)
                ResetMotion();
            if (!_record.ApprovedByDefault) _tag.SetActive(false);
            if (!_pressing) Show(_ready);
        }

        public void OnAuthorityStateUpdated(GameState state)
        {
            _hasAuthority = true;
            SetAuthority(state);
            if (_rattling && !_opponentTurn) ResetMotion();
            if (!_pressing) Show(_ready);
        }

        private void SetAuthority(GameState state)
        {
            bool playing = state != null && state.Phase == TurnPhase.Main && state.Outcome == MatchOutcome.InProgress;
            _ready = _input != null && playing && state.ActivePlayer == _local;
            _opponentTurn = playing && state.ActivePlayer != _local;
            _authorityTurn = state == null ? 0 : state.TurnNumber;
        }

        public void OnMatchEvent(IReadOnlyList<GameEvent> events)
        {
            foreach (GameEvent e in events)
            {
                if (!_record.OnEvent(e)) continue;
                ResetMotion();
                StartCoroutine(Press(automatic: true));
            }
        }

        void IPointerPressTarget.OnPointerDown()
        {
            if (_pressing || _input == null || _input.BlocksInput) return;
            if (_opponentTurn) StartCoroutine(Rattle());
            else if (IsReady) StartCoroutine(Press(automatic: false));
        }
        private IEnumerator Press(bool automatic)
        {
            _pressing = true;
            _automatic = automatic;
            int turn = _authorityTurn;
            float pace = automatic ? 2.8f : 1f;
            _latch.SetActive(false);
            _tag.SetActive(false);
            if (automatic) _renderer.enabled = false;
            Vector3 normal = _wax.up;
            Bounds waxBounds = _wax.GetComponent<MeshFilter>().sharedMesh.bounds;
            Vector3 waxSurface = _wax.TransformPoint(new Vector3(waxBounds.center.x, waxBounds.max.y, waxBounds.center.z));
            // Work in the inclined wax plane so the face lands flat and lifts away normally.
            Bounds stampBounds = _stamp.GetComponent<MeshFilter>().sharedMesh.bounds;
            _stamp.rotation = _stampPressRotation;
            Vector3 faceOffset = _stamp.TransformVector(new Vector3(stampBounds.center.x, stampBounds.min.y, stampBounds.center.z));
            _stamp.rotation = _stampRotation;
            Vector3 target = waxSurface + normal * 0.02f - faceOffset;
            Vector3 lifted = target + normal * 0.75f;
            yield return MoveStamp(_stampRest, lifted, 0.22f * pace, _stampPressRotation);
            yield return MoveStamp(lifted, target, 0.12f * pace, _stampPressRotation);
            // A timeout may win while a manual press is descending. Never send a stale
            // click, or let an interrupted animation issue a command on a later local turn.
            if (!automatic && (!_ready || turn != _authorityTurn || _input.BlocksInput))
            {
                RestoreMotion();
                Show(_ready);
                yield break;
            }
            _renderer.enabled = true;
            _tag.SetActive(automatic && _record.ApprovedByDefault);
            if (!automatic) _input.OnEndTurnClicked();
            UiSfx.Play(UiSfx.WaxStamp);
            for (float t = 0; t < 0.18f; t += Time.unscaledDeltaTime)
            {
                float press = Mathf.Sin(t / 0.18f * Mathf.PI);
                _wax.localScale = Vector3.Scale(_scale, new Vector3(1f + press * 0.035f, 1f - press * 0.14f, 1f + press * 0.035f));
                yield return null;
            }
            _wax.localPosition = _rest; _wax.localScale = _scale;
            yield return MoveStamp(target, lifted, 0.14f * pace, _stampPressRotation);
            yield return MoveStamp(lifted, _stampRest, 0.25f * pace, _stampRotation);
            _stamp.rotation = _stampRotation;
            yield return new WaitForSecondsRealtime(0.35f);
            _pressing = false; _automatic = false; Show(_ready);
        }
        private IEnumerator MoveStamp(Vector3 from, Vector3 to, float duration, Quaternion rotation)
        {
            Quaternion fromRotation = _stamp.rotation;
            for (float t = 0; t < duration; t += Time.unscaledDeltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / duration);
                _stamp.position = Vector3.Lerp(from, to, k);
                _stamp.rotation = Quaternion.Slerp(fromRotation, rotation, k);
                yield return null;
            }
            _stamp.position = to;
            _stamp.rotation = rotation;
        }
        private void Show(bool ready)
        {
            _renderer.enabled = _presentedOpponentTurn || _record.ApprovedByDefault;
            _tag.SetActive(_record.ApprovedByDefault);
            _latch.SetActive(_opponentTurn);
            _stampRenderer.GetPropertyBlock(_properties);
            // glTFast uses baseColorFactor; the URP Lit material uses _BaseColor. Both are
            // assigned so re-importing the model cannot silently lose turn feedback.
            Color color = ready ? Color.white : new Color(0.60f, 0.55f, 0.56f);
            _properties.SetColor("baseColorFactor", color);
            _properties.SetColor("_BaseColor", color);
            _stampRenderer.SetPropertyBlock(_properties);
        }

        private IEnumerator Rattle()
        {
            _pressing = true;
            _rattling = true;
            _rattleAudio.Play();
            for (float t = 0; t < 0.32f; t += Time.unscaledDeltaTime)
            {
                float shake = Mathf.Sin(t * 85f) * Mathf.Pow(1f - t / 0.32f, 2f);
                _stamp.position = _stampRest + Vector3.right * (shake * 0.035f);
                _stamp.rotation = Quaternion.AngleAxis(shake * 2f, Vector3.forward) * _stampRotation;
                yield return null;
            }
            RestoreMotion();
            Show(_ready);
        }

        private void ResetMotion()
        {
            StopAllCoroutines();
            if (_rattleAudio != null) _rattleAudio.Stop();
            RestoreMotion();
        }

        private void RestoreMotion()
        {
            _stamp.SetPositionAndRotation(_stampRest, _stampRotation);
            _wax.localPosition = _rest;
            _wax.localScale = _scale;
            _pressing = _automatic = _rattling = false;
        }

        private void BuildReceiptAndLatch()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            _paperMaterial = new Material(shader) { name = "Default approval paper" };
            _paperMaterial.SetColor("_BaseColor", new Color(0.91f, 0.82f, 0.62f));
            _paperMaterial.SetFloat("_Smoothness", 0.08f);
            _brassMaterial = new Material(shader) { name = "Stamper latch brass" };
            _brassMaterial.SetColor("_BaseColor", new Color(0.58f, 0.36f, 0.10f));
            _brassMaterial.SetFloat("_Metallic", 0.72f);
            _brassMaterial.SetFloat("_Smoothness", 0.48f);

            Vector3 normal = _wax.up;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
            Quaternion face = Quaternion.LookRotation(-normal, up);
            Bounds waxBounds = _wax.GetComponent<MeshFilter>().sharedMesh.bounds;
            Vector3 surface = _wax.TransformPoint(new Vector3(waxBounds.center.x, waxBounds.max.y, waxBounds.center.z));
            _tag = new GameObject("APPROVED BY DEFAULT receipt");
            _tag.transform.SetParent(transform, true);
            _tag.transform.SetPositionAndRotation(surface + normal * 0.045f - up * 0.55f, face);
            Part(_tag.transform, "Paper tag", Vector3.zero, new Vector3(1.65f, 0.72f, 0.016f), _paperMaterial);
            Part(_tag.transform, "Tag tie", new Vector3(0f, 0.44f, 0f), new Vector3(0.025f, 0.24f, 0.02f), _paperMaterial);
            var label = new GameObject("Approval ink").AddComponent<TextMeshPro>();
            label.transform.SetParent(_tag.transform, false);
            label.transform.localPosition = new Vector3(0f, 0f, -0.018f);
            BoardController.ApplyUiFont(label);
            label.text = "APPROVED\nBY DEFAULT";
            label.fontSize = 2.4f;
            label.fontStyle = FontStyles.Bold;
            label.color = new Color(0.29f, 0.10f, 0.08f);
            label.alignment = TextAlignmentOptions.Center;
            label.rectTransform.sizeDelta = new Vector2(1.53f, 0.66f);
            label.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _latch = new GameObject("Opponent turn brass latch");
            _latch.transform.SetParent(transform, true);
            Bounds stampBounds = _stamp.GetComponent<MeshFilter>().sharedMesh.bounds;
            Vector3 foot = _stamp.TransformPoint(new Vector3(stampBounds.center.x, stampBounds.min.y, stampBounds.center.z));
            _latch.transform.SetPositionAndRotation(foot + _stamp.up * 0.10f, face);
            Part(_latch.transform, "Latch post", new Vector3(0.34f, 0.06f, 0f), new Vector3(0.10f, 0.30f, 0.14f), _brassMaterial);
            Part(_latch.transform, "Latch arm", new Vector3(0.16f, 0.19f, -0.07f), new Vector3(0.45f, 0.08f, 0.14f), _brassMaterial);

            // Three short, damped metal contacts. Local audio shares the user's SFX mixer.
            const int rate = 24000;
            var samples = new float[(int)(rate * 0.32f)];
            for (int i = 0; i < samples.Length; i++)
                for (int hit = 0; hit < 3; hit++)
                {
                    float t = i / (float)rate - hit * 0.075f;
                    if (t >= 0f)
                        samples[i] += 0.14f * (1f - hit * 0.25f) * Mathf.Exp(-65f * t)
                            * (Mathf.Sin(t * 1400f * Mathf.PI * 2f) + 0.45f * Mathf.Sin(t * 2317f * Mathf.PI * 2f));
                }
            _rattleClip = AudioClip.Create("Brass latch rattle", samples.Length, 1, rate, false);
            _rattleClip.SetData(samples, 0);
            _rattleAudio = gameObject.AddComponent<AudioSource>();
            _rattleAudio.playOnAwake = false;
            _rattleAudio.outputAudioMixerGroup = AudioSettings.SfxGroup;
            _rattleAudio.clip = _rattleClip;
        }

        private static void Part(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = size;
            var collider = part.GetComponent<Collider>();
            collider.enabled = false;
            DestroyObj(collider);
            part.GetComponent<Renderer>().sharedMaterial = material;
        }

        private void OnDisable()
        {
            if (_stamp != null) ResetMotion();
        }

        private void OnDestroy()
        {
            DestroyObj(_paperMaterial);
            DestroyObj(_brassMaterial);
            DestroyObj(_rattleClip);
        }

        // DeviceLayoutVerify builds this table in edit mode, where Destroy is refused with an
        // error per call and the check fails on the errors.
        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
