using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    // World-space card visual (board-and-vfx.md §1, interfaces.md §5.1).
    // Subscribes to ICardLiveState.Changed; never polls per-frame.
    // For Phase C step 2: primitive Unity visuals only — no shaders yet.
    // Setup(…) is called by BoardController when creating presenters at runtime.
    public sealed class CardPresenter : MonoBehaviour, ICardPresenter
    {
        // Populated via Setup() (runtime) or serialized refs in a prefab (editor).
        [SerializeField] private TextMeshPro nameLabel;
        [SerializeField] private TextMeshPro costLabel;
        [SerializeField] private TextMeshPro attackLabel;
        [SerializeField] private TextMeshPro healthLabel;
        [SerializeField] private TextMeshPro keywordLabel;
        [SerializeField] private Renderer cardBackground;

        private static readonly Color NormalColor   = new Color(0.22f, 0.22f, 0.32f);
        private static readonly Color BuffColor     = new Color(0.20f, 0.80f, 0.25f);
        private static readonly Color DamageColor   = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color ShieldColor   = new Color(0.95f, 0.88f, 0.15f);
        private static readonly Color FrozenColor   = new Color(0.40f, 0.60f, 0.95f);
        private static readonly Color TauntColor    = new Color(0.80f, 0.50f, 0.10f);
        private static readonly Color SelectedColor = new Color(1.00f, 0.85f, 0.10f); // gold

        private CardDefinition  _def;
        private ICardLiveState  _live;
        private MaterialPropertyBlock _mpb;
        private bool _isSelected;

        // Called by BoardController.CreatePresenter when building the visual hierarchy at runtime.
        public void Setup(TextMeshPro name, TextMeshPro cost, TextMeshPro attack, TextMeshPro health,
                          TextMeshPro keywords, Renderer bg)
        {
            nameLabel    = name;
            costLabel    = cost;
            attackLabel  = attack;
            healthLabel  = health;
            keywordLabel = keywords;
            cardBackground = bg;
        }

        public void Bind(CardDefinition staticDef, ICardLiveState liveState)
        {
            _def  = staticDef;
            _live = liveState;
            _live.Changed += OnChanged;
            ApplyAll();
        }

        public void Unbind()
        {
            if (_live == null) return;
            _live.Changed -= OnChanged;
            _live = null;
            _def  = null;
        }

        // Called by InputRouter to highlight this card as the active selection.
        public void SetSelectionHighlight(bool selected)
        {
            _isSelected = selected;
            ApplyAuras();
        }

        private void OnDestroy() => Unbind();

        private void OnChanged(CardChange what)
        {
            if ((what & CardChange.Stats)    != 0) RecolorStats();
            if ((what & CardChange.Keywords) != 0) RebuildKeywords();
            if ((what & CardChange.Auras)    != 0) ApplyAuras();
        }

        private void ApplyAll()
        {
            if (_def == null) return;
            if (nameLabel != null) nameLabel.text = _def.Name;
            if (costLabel != null) costLabel.text = _def.Cost.ToString();
            RecolorStats();
            RebuildKeywords();
            ApplyAuras();
        }

        private void RecolorStats()
        {
            if (_live == null) return;

            if (attackLabel != null)
            {
                int baseAtk = _def?.Attack ?? _live.Attack;
                attackLabel.text  = _live.Attack.ToString();
                attackLabel.color = _live.Attack > baseAtk ? BuffColor
                                  : _live.Attack < baseAtk ? DamageColor
                                  : Color.white;
            }

            if (healthLabel != null)
            {
                int baseHp = _def?.Health ?? _live.MaxHealth;
                healthLabel.text  = _live.Health.ToString();
                healthLabel.color = _live.Health < _live.MaxHealth ? DamageColor
                                  : _live.MaxHealth > baseHp      ? BuffColor
                                  : Color.white;
            }
        }

        private void RebuildKeywords()
        {
            if (_live == null || keywordLabel == null) return;
            var sb = new StringBuilder();
            foreach (Keyword kw in _live.Keywords)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(kw.ToString());
            }
            keywordLabel.text = sb.ToString();
        }

        private void ApplyAuras()
        {
            if (_live == null || cardBackground == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            Color c;
            if      (_isSelected)                          c = SelectedColor;
            else if ((_live.Auras & AuraSet.Frozen) != 0) c = FrozenColor;
            else if ((_live.Auras & AuraSet.Shield) != 0) c = ShieldColor;
            else if ((_live.Auras & AuraSet.Taunt)  != 0) c = TauntColor;
            else                                           c = NormalColor;

            cardBackground.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            cardBackground.SetPropertyBlock(_mpb);
        }
    }
}
