using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
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
            _label = gameObject.GetComponent<TextMeshPro>() ?? gameObject.AddComponent<TextMeshPro>();
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
            var rows = OngoingEffectStatusModel.Build(state, _database, _viewer);
            if (rows.Count == 0)
            {
                _label.text = "";
                _label.enabled = false;
                return;
            }

            var text = new StringBuilder("ACTIVE EFFECTS");
            foreach (OngoingEffectStatus row in rows)
                text.Append('\n').Append(row.SourceName).Append(" · ")
                    .Append(row.Owner).Append(" · ").Append(row.Remaining);
            _label.text = text.ToString();
            _label.enabled = true;
        }
    }
}
