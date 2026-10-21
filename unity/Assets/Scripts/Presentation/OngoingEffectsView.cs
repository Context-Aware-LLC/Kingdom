using System.Text;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Compact, read-only HUD list of the ongoing effects visible in projected state.</summary>
    public sealed class OngoingEffectsView : MonoBehaviour
    {
        private ICardDatabase _database;
        private PlayerId _viewer;
        private TextMeshPro _label;

        public static OngoingEffectsView Create(PlayerId viewer, ICardDatabase database)
        {
            var go = new GameObject("OngoingEffectsStatus");
            // Left side rail, clear of the seven-slot board rows and the bottom hero cluster.
            go.transform.position = new Vector3(-5.35f, 0.75f, -0.2f);
            var view = go.AddComponent<OngoingEffectsView>();
            view.Init(viewer, database);
            return view;
        }

        public void Init(PlayerId viewer, ICardDatabase database)
        {
            _viewer = viewer;
            _database = database;
            // TryGetComponent, not `??` — see CardSoundDirector.Init (Sam, 2026-08-27).
            if (!gameObject.TryGetComponent(out TextMeshPro label))
                label = gameObject.AddComponent<TextMeshPro>();
            _label = label;
            var font = BoardController.BodyFont();
            if (font != null) _label.font = font;
            _label.fontSize = 1.05f;
            _label.alignment = TextAlignmentOptions.TopLeft;
            _label.color = new Color(0.96f, 0.88f, 0.62f);
            _label.rectTransform.sizeDelta = new Vector2(3.2f, 3.0f);
            _label.textWrappingMode = TextWrappingModes.Normal;
            _label.overflowMode = TextOverflowModes.Ellipsis;
            _label.GetComponent<Renderer>().sortingOrder = 45;
            _label.enabled = false;
        }

        public void OnStateUpdated(GameState state)
        {
            if (_database == null || _label == null || state == null) return;
            var rows = OngoingEffectStatusModel.Build(state, _database, _viewer, LocWords.Instance);
            if (rows.Count == 0)
            {
                _label.text = "";
                _label.enabled = false;
                return;
            }

            var text = new StringBuilder(Loc.Get("effects_panel_title"));
            foreach (OngoingEffectStatus row in rows)
                text.Append('\n').Append(Loc.Format("effects_row_format",
                    row.SourceName, row.Owner, row.Remaining));
            _label.text = text.ToString();
            _label.enabled = true;
        }
    }

    /// <summary>The status-row words in the player's language.</summary>
    internal sealed class LocWords : IOngoingEffectWords
    {
        public static readonly LocWords Instance = new LocWords();
        public string You => Loc.Get("common_you");
        public string Opponent => Loc.Get("common_opponent");
        public string Permanent => Loc.Get("effects_remaining_permanent");
        public string UnknownLifetime => Loc.Get("effects_remaining_unknown");
        public string SourceFallback(int cardId) => Loc.Format("effects_source_fallback", cardId);
        public string TriggersRemaining(int uses) => Loc.Plural("effects_remaining_triggers", uses, uses);
    }
}
