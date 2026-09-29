using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// End-of-match presentation: a translucent scrim over the board with the verdict
    /// ("VICTORY" / "DEFEAT" / "DRAW") from the local seat's perspective. Built procedurally
    /// on demand by NetworkedGameBootstrap's OnMatchEnded handler — same primitives-only approach
    /// as HeroPlateView.
    /// The banner remains visible briefly, then returns every match mode to the main menu.
    /// </summary>
    public static class MatchEndBanner
    {
        private static readonly Color ScrimColor   = new Color(0.02f, 0.02f, 0.05f, 0.72f);
        private static readonly Color VictoryColor = new Color(0.95f, 0.78f, 0.25f);
        private static readonly Color DefeatColor  = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color DrawColor    = new Color(0.75f, 0.75f, 0.78f);

        /// <summary>Spawn the banner. localSeat is the seat the verdict is judged from (P0 in hot-seat).</summary>
        public static GameObject Show(MatchOutcome outcome, int localSeat)
        {
            (string verdict, Color color) = Judge(outcome, localSeat);

            var root = new GameObject("MatchEndBanner");
            root.transform.position = new Vector3(0f, 0f, -2f); // in front of every board layer

            // Scrim — SpriteRenderer (not a Quad) so the default sprite material honors alpha.
            var scrimGo = new GameObject("Scrim");
            scrimGo.transform.SetParent(root.transform, false);
            var scrim = scrimGo.AddComponent<SpriteRenderer>();
            scrim.sprite = Sprite.Create(Texture2D.whiteTexture,
                new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), pixelsPerUnit: 4f);
            scrim.color = ScrimColor;
            scrimGo.transform.localScale = new Vector3(40f, 24f, 1f);
            scrim.sortingOrder = 100;

            // Verdict headline.
            var headline = MakeLabel(root.transform, "Verdict", new Vector3(0f, 0.5f, -0.01f), 12f, color);
            headline.text = verdict;
            headline.fontStyle = FontStyles.Bold;

            MakeRule(root.transform, new Vector3(0f, -0.28f, -0.01f), color);

            // Say what the result means to this player. Engine seat numbers are useful in logs,
            // but they turn the emotional payoff into implementation language on screen.
            var subtitle = MakeLabel(root.transform, "Outcome", new Vector3(0f, -1.1f, -0.01f), 2.5f, Color.white);
            subtitle.text = ResultDetail(outcome, localSeat);

            var footer = MakeLabel(root.transform, "Return", new Vector3(0f, -1.85f, -0.01f), 1.75f,
                new Color(0.72f, 0.75f, 0.82f));
            footer.text = "Returning to the Kingdom…";

            root.AddComponent<MatchEndMenuReturn>();
            return root;
        }

        private static (string, Color) Judge(MatchOutcome outcome, int localSeat) => outcome switch
        {
            MatchOutcome.Player0Win when localSeat == 0 => ("VICTORY", VictoryColor),
            MatchOutcome.Player1Win when localSeat == 1 => ("VICTORY", VictoryColor),
            MatchOutcome.Draw                           => ("DRAW", DrawColor),
            _                                           => ("DEFEAT", DefeatColor),
        };

        private static string ResultDetail(MatchOutcome outcome, int localSeat)
        {
            if (outcome == MatchOutcome.Draw) return "Both heroes fell";

            bool localWon = outcome == MatchOutcome.Player0Win && localSeat == 0
                            || outcome == MatchOutcome.Player1Win && localSeat == 1;
            return localWon ? "You won the match" : "Your opponent won the match";
        }

        private static void MakeRule(Transform parent, Vector3 localPos, Color color)
        {
            var go = new GameObject("AccentRule");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(4.8f, 0.035f, 1f);

            var rule = go.AddComponent<SpriteRenderer>();
            rule.sprite = Sprite.Create(Texture2D.whiteTexture,
                new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), pixelsPerUnit: 4f);
            rule.color = color;
            rule.sortingOrder = 101;
        }

        private static TextMeshPro MakeLabel(Transform parent, string goName, Vector3 localPos, float fontSize, Color color)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize  = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;
            tmp.rectTransform.sizeDelta = new Vector2(16f, fontSize * 0.3f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.sortingOrder     = 101; // above the scrim
            return tmp;
        }
    }
}
