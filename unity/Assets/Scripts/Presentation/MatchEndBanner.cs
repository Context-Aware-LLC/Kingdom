using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Quests;
using Kingdom.Net.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// End-of-match presentation: the proclamation (<see cref="MatchProclamation"/>) unrolled over
    /// the board, with the verdict from the local seat's perspective, why it ended, the spoils and
    /// the Council's Findings. Built on demand by NetworkedGameBootstrap's OnMatchEnded handler as
    /// its own UIDocument, the Resources-loaded pattern <see cref="ConnectionFailedOverlay"/> uses.
    /// <see cref="MatchEndSequence"/> paces what follows — spoils, voice line, linger — and
    /// then offers Play Again (a queued match only) or returns to deck selection.
    /// </summary>
    public static class MatchEndBanner
    {
        /// <summary>Above the match HUD's own panel, so nothing on the board reads over the sheet.</summary>
        private const int SortingOrder = 100;

        /// <summary>Spawn the banner. localSeat is the seat the verdict is judged from.</summary>
        /// <param name="quests">The quests this match advanced, still being read when the banner
        /// goes up; null shows none.</param>
        /// <param name="gold">The Gold this match paid, still being read; null shows none.</param>
        /// <param name="playVoiceLine">Plays the hero's win/lose line when the sequence reaches it.</param>
        /// <param name="reason">The server's end reason; null when it never arrived.</param>
        /// <param name="findings">The Council's Findings this seat committed, from the match-end
        /// message; null or empty stamps none.</param>
        /// <param name="rematchDeckId">The deck this match was queued with; Play Again requeues it.
        /// Null or empty offers no Play Again, only the way back to deck selection.</param>
        /// <param name="commission">This seat's King's commission final, from the match-end
        /// message; null writes no line.</param>
        public static GameObject Show(MatchOutcome outcome, int localSeat,
                                      Task<IReadOnlyList<QuestAdvance>> quests = null,
                                      Action playVoiceLine = null,
                                      MatchEndReason? reason = null,
                                      IReadOnlyList<string> findings = null,
                                      Task<long> gold = null,
                                      string rematchDeckId = null,
                                      CommissionFinal commission = null)
        {
            var root = new GameObject("MatchEndBanner");
            var doc = root.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("MatchProclamation");
            doc.sortingOrder    = SortingOrder;

            UiLocalizer.Attach(doc.rootVisualElement);
            MatchProclamation proclamation = null;
            if (doc.rootVisualElement != null)
                proclamation = new MatchProclamation(doc.rootVisualElement, outcome, localSeat, reason, findings,
                                                     commission: commission);
            else
                Debug.LogError("[MatchEnd] No rootVisualElement: is MatchProclamation.uxml in a Resources folder?");

            root.AddComponent<MatchEndSequence>().Init(proclamation, quests, gold, playVoiceLine, rematchDeckId);
            return root;
        }
    }
}
