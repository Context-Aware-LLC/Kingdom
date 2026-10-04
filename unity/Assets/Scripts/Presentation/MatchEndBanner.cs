using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Quests;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// End-of-match presentation: a translucent scrim over the board with the verdict
    /// ("VICTORY" / "DEFEAT" / "DRAW") from the local seat's perspective. Built procedurally
    /// on demand by NetworkedGameBootstrap's OnMatchEnded handler — same primitives-only approach
    /// as HeroPlateView.
    /// <see cref="MatchEndSequence"/> paces what follows — quests, voice line, linger — and
    /// returns every match mode to deck selection.
    /// </summary>
    public static class MatchEndBanner
    {
        private static readonly Color ScrimColor   = new Color(0.02f, 0.02f, 0.05f, 0.72f);
        private static readonly Color VictoryColor = new Color(0.95f, 0.78f, 0.25f);
        private static readonly Color DefeatColor  = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color DrawColor    = new Color(0.75f, 0.75f, 0.78f);

        // Quest cards: one row under the outcome line, in world units. Three is the server's
        // whole window (today plus two days back), so the row never needs to wrap.
        private const int   MaxQuestCards = 3;
        private const float QuestCardW    = 3.6f;
        private const float QuestCardH    = 1.25f;
        private const float QuestCardGap  = 0.3f;
        private const float QuestRowY     = -2.75f;
        private static readonly Color QuestPanelColor = new Color(0.08f, 0.08f, 0.12f, 0.92f);
        private static readonly Color QuestTrackColor = new Color(0.22f, 0.22f, 0.28f);
        private static readonly Color QuestIdleColor  = new Color(0.72f, 0.75f, 0.82f);

        /// <summary>Spawn the banner. localSeat is the seat the verdict is judged from (P0 in hot-seat).</summary>
        /// <param name="quests">The quests this match advanced, still being read when the banner
        /// goes up; null shows none.</param>
        /// <param name="playVoiceLine">Plays the hero's win/lose line when the sequence reaches it.</param>
        public static GameObject Show(MatchOutcome outcome, int localSeat,
                                      Task<IReadOnlyList<QuestAdvance>> quests = null,
                                      Action playVoiceLine = null)
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
            BoardController.ApplyUiFont(headline);   // again: Bold picks the bold cut

            MakeRule(root.transform, new Vector3(0f, -0.28f, -0.01f), color);

            // Say what the result means to this player. Engine seat numbers are useful in logs,
            // but they turn the emotional payoff into implementation language on screen.
            var subtitle = MakeLabel(root.transform, "Outcome", new Vector3(0f, -1.1f, -0.01f), 2.5f, Color.white);
            subtitle.text = ResultDetail(outcome, localSeat);

            // Empty until the sequence lets the player leave, and below the quest row so the two
            // never share a line.
            var footer = MakeLabel(root.transform, "Return", new Vector3(0f, -3.95f, -0.01f), 1.75f,
                QuestIdleColor);
            footer.text = "";

            root.AddComponent<MatchEndSequence>().Init(quests, playVoiceLine, footer);
            return root;
        }

        /// <summary>Lay the advanced quests out as a centred row of cards under the outcome line.
        /// A quest this match completed is lit in gold and names its reward; one still in
        /// progress shows where it stands and how far this match moved it.</summary>
        public static void ShowQuests(Transform root, IReadOnlyList<QuestAdvance> advanced)
        {
            int count = Mathf.Min(advanced.Count, MaxQuestCards);
            float span = count * QuestCardW + (count - 1) * QuestCardGap;

            for (int i = 0; i < count; i++)
            {
                QuestAdvance advance = advanced[i];
                QuestEntry quest = advance.Quest;
                Color accent = advance.CompletedNow ? VictoryColor : QuestIdleColor;

                var card = new GameObject($"Quest_{quest.Id}");
                card.transform.SetParent(root, false);
                card.transform.localPosition = new Vector3(
                    -span * 0.5f + QuestCardW * 0.5f + i * (QuestCardW + QuestCardGap), QuestRowY, -0.01f);

                MakeBlock(card.transform, "Panel", Vector3.zero,
                    new Vector2(QuestCardW, QuestCardH), QuestPanelColor, 101);
                MakeBlock(card.transform, "Accent", new Vector3(0f, QuestCardH * 0.5f - 0.02f, 0f),
                    new Vector2(QuestCardW, 0.04f), accent, 102);

                var title = MakeQuestLabel(card.transform, "Title", 0.30f, 1.9f, Color.white);
                title.text = quest.Title;

                var detail = MakeQuestLabel(card.transform, "Detail", -0.04f, 1.6f, accent);
                detail.text = advance.CompletedNow
                    ? $"COMPLETE   +{quest.RewardGold} gold"
                    : $"{quest.Progress} / {quest.Goal}   (+{quest.Progress - advance.PreviousProgress})";

                float trackW = QuestCardW * 0.84f;
                float fillW = trackW * Mathf.Clamp01((float)quest.Progress / quest.Goal);
                MakeBlock(card.transform, "Track", new Vector3(0f, -0.40f, 0f),
                    new Vector2(trackW, 0.10f), QuestTrackColor, 102);
                MakeBlock(card.transform, "Fill", new Vector3((fillW - trackW) * 0.5f, -0.40f, 0f),
                    new Vector2(fillW, 0.10f), accent, 103);
            }
        }

        private static TextMeshPro MakeQuestLabel(Transform card, string goName, float y, float fontSize, Color color)
        {
            var label = MakeLabel(card, goName, new Vector3(0f, y, -0.01f), fontSize, color);
            // Held to the card: a long quest title shrinks rather than running into its neighbour.
            label.rectTransform.sizeDelta = new Vector2(QuestCardW * 0.92f, fontSize * 0.3f);
            label.enableAutoSizing = true;
            label.fontSizeMax = fontSize;
            label.fontSizeMin = fontSize * 0.5f;
            label.sortingOrder = 104; // above the panel, accent and progress bar
            return label;
        }

        private static void MakeBlock(Transform parent, string goName, Vector3 localPos, Vector2 size,
                                      Color color, int sortingOrder)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);

            var block = go.AddComponent<SpriteRenderer>();
            block.sprite = Sprite.Create(Texture2D.whiteTexture,
                new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), pixelsPerUnit: 4f);
            block.color = color;
            block.sortingOrder = sortingOrder;
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
            BoardController.ApplyUiFont(tmp);
            return tmp;
        }
    }
}
