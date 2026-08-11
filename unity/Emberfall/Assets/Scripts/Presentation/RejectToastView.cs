using System.Collections.Generic;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Rejection-feedback toasts (dev-docs/ui/client-surfaces.md:15). World-space,
    /// bottom-right, built procedurally like EndTurnButtonView. One line per
    /// RejectReason from the §6.5 copy table — the single source of player-facing
    /// rejection copy. Max 2 visible (oldest evicted), auto-dismiss, click to
    /// dismiss; a repeat of the visible reason refreshes its timer instead of
    /// stacking. The "Also do" column of §6.5 (shakes, dims, highlights) is the
    /// attack-affordances workstream, not this view.
    /// </summary>
    public sealed class RejectToastView : MonoBehaviour
    {
        private const float ToastW = 4.2f;
        private const float ToastH = 0.5f;
        private const float StackGap = 0.6f;
        private const float LifeSeconds = 3.0f;
        private const int MaxVisible = 2;

        private static readonly Color PlateColor = new Color(0.30f, 0.15f, 0.14f);

        private sealed class Entry
        {
            public GameObject Root;
            public string Text;
            public float ExpiresAt;
        }

        // Newest first; index 0 sits at the anchor, older entries push up.
        private readonly List<Entry> _entries = new();

        public void Show(RejectReason reason) => Show(CopyFor(reason));

        private void Show(string text)
        {
            var dup = _entries.Find(e => e.Text == text);
            if (dup != null)
            {
                dup.ExpiresAt = Time.time + LifeSeconds;
                return;
            }

            var entry = new Entry
            {
                Root = BuildToast(text),
                Text = text,
                ExpiresAt = Time.time + LifeSeconds,
            };
            _entries.Insert(0, entry);

            while (_entries.Count > MaxVisible)
                Dismiss(_entries[_entries.Count - 1]);
            Relayout();
        }

        private void Update()
        {
            bool changed = false;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (Time.time >= _entries[i].ExpiresAt)
                {
                    Dismiss(_entries[i]);
                    changed = true;
                }
            }
            if (changed) Relayout();
        }

        internal void DismissByRoot(GameObject root)
        {
            var entry = _entries.Find(e => e.Root == root);
            if (entry == null) return;
            Dismiss(entry);
            Relayout();
        }

        private void Dismiss(Entry entry)
        {
            _entries.Remove(entry);
            if (entry.Root != null) Destroy(entry.Root);
        }

        private void Relayout()
        {
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].Root.transform.localPosition = new Vector3(0f, i * StackGap, 0f);
        }

        private GameObject BuildToast(string text)
        {
            var root = new GameObject("Toast");
            root.transform.SetParent(transform, false);

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(root.transform, false);
            faceGo.transform.localScale = new Vector3(ToastW, ToastH, 1f);
            Destroy(faceGo.GetComponent<MeshCollider>());
            var face = faceGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(face, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            face.SetPropertyBlock(mpb);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            var label = labelGo.AddComponent<TextMeshPro>();
            label.fontSize = 1.2f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.rectTransform.sizeDelta = new Vector2(ToastW, ToastH);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.text = text;

            var col = root.AddComponent<BoxCollider>();
            col.size = new Vector3(ToastW, ToastH, 0.1f);
            root.AddComponent<RejectToastClickCatcher>().Owner = this;

            return root;
        }

        // §6.5 table, 1:1 with the real 16-value enum. Engineering owns the enum;
        // the table and this switch move in lockstep when it changes.
        private static string CopyFor(RejectReason reason) => reason switch
        {
            RejectReason.NotYourTurn          => "It's not your turn.",
            RejectReason.WrongPhase           => "Not right now.",
            RejectReason.NotEnoughMana        => "Not enough mana.",
            RejectReason.IllegalTarget        => "Can't target that.",
            RejectReason.NotInHand            => "That card's not in your hand.",
            RejectReason.BoardFull            => "Your board is full (7).",
            RejectReason.HeroPowerAlreadyUsed => "Hero Power already used.",
            RejectReason.WeaponMissing        => "No weapon equipped.",
            RejectReason.HasSummoningSickness => "Can't attack yet this turn.",
            RejectReason.NoAttacksLeft        => "Already attacked this turn.",
            // "Frozen", not the enum's spelling: FROST prints as "Freeze" on the card face
            // (KeywordSemantics.AuthoredName), and a toast is the same player reading the same
            // mechanic seconds later. The enum id stays AttackerFrosted — it is wire state.
            RejectReason.AttackerFrosted       => "Frozen — can't attack.",
            RejectReason.AlreadyMulliganed    => "You've already swapped.",
            RejectReason.GameOver             => "The match is over.",
            RejectReason.RateLimited          => "Slow down a moment.",
            _                                 => "Can't do that right now.", // UnknownIntent / NotImplemented
        };
    }

    /// <summary>Per-toast click-to-dismiss relay (§6.4: click to dismiss).</summary>
    public sealed class RejectToastClickCatcher : MonoBehaviour
    {
        internal RejectToastView Owner;
        private void OnMouseDown() => Owner?.DismissByRoot(gameObject);
    }
}
