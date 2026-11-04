using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Quests;
using Kingdom.Net.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// What the result screen says, written on the herald's scroll (whimsy pass 2026-10-17, #2): the
    /// verdict and why the match ended, the spoils it paid, and the Council's Findings — the bad
    /// ideas this match committed, each stamped. <see cref="MatchEndSequence"/> decides WHEN each
    /// part appears; this class only knows how to write it.
    ///
    /// <para><b>The findings arrive with the verdict</b>, on the match-end message, so they are on
    /// the sheet as it opens and stamped one after another once it stops. The spoils arrive later:
    /// Gold and quests are two HTTP reads behind the server's reward queues, so their column
    /// appears when the sequence has them and the sheet grows to take it.</para>
    ///
    /// <para><b>A finding this build does not know is skipped</b>, not printed by id: a newer server
    /// can name one a store build has no text for, and a raw id is the one thing this screen must
    /// never show.</para>
    /// </summary>
    public sealed class MatchProclamation
    {
        /// <summary>More than this many findings and the rest are counted, not listed: the sheet is
        /// sized to fit the 1080 authored px panel with four.</summary>
        public const int MaxFindingsShown = 4;

        private const string HiddenColumnClass = "proc-column--hidden";
        private const string HiddenActionsClass = "proc-actions--hidden";
        private const int StampDelayMs = 160;   // the first wax, after the sheet stops
        private const int StampGapMs = 420;     // between one finding's wax and the next
        private const float SealSize = 64f;

        private readonly VisualElement _root;
        private readonly ProclamationScroll _scroll;
        private readonly VisualElement _spoils;
        private readonly Label _gold;
        private readonly VisualElement _quests;
        private readonly VisualElement _findingsColumn;
        private readonly VisualElement _findings;
        private readonly Label _footer;
        private readonly VisualElement _actions;
        private readonly Button _playAgain;
        private readonly IUiSpriteProvider _sprites;
        private readonly List<VisualElement> _findingRows = new List<VisualElement>();

        /// <param name="commission">The King's commission final for this seat (#421), off the
        /// match-end message. Null writes nothing: the server sends one only when the score counts.</param>
        public MatchProclamation(VisualElement root, MatchOutcome outcome, int localSeat,
                                 MatchEndReason? reason, IReadOnlyList<string> findings,
                                 IUiSpriteProvider sprites = null, CommissionFinal commission = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
#if UNITY_EDITOR
            _sprites = sprites ?? new EditorUiSpriteProvider();
#else
            _sprites = sprites ?? new AddressableUiSpriteProvider();
#endif
            _spoils = root.Q<VisualElement>("proc-spoils");
            _gold = root.Q<Label>("proc-gold");
            _quests = root.Q<VisualElement>("proc-quests");
            _findingsColumn = root.Q<VisualElement>("proc-findings-column");
            _findings = root.Q<VisualElement>("proc-findings");
            _footer = root.Q<Label>("proc-footer");
            _actions = root.Q<VisualElement>("proc-actions");
            _playAgain = root.Q<Button>("proc-play-again");

            (string verdict, string verdictClass) = Judge(outcome, localSeat);
            var verdictLabel = root.Q<Label>("proc-verdict");
            if (verdictLabel != null)
            {
                verdictLabel.text = verdict;
                verdictLabel.AddToClassList(verdictClass);
            }
            var reasonLabel = root.Q<Label>("proc-reason");
            if (reasonLabel != null) reasonLabel.text = ResultDetail(outcome, localSeat, reason);
            WriteCommission(root.Q<Label>("proc-commission"), commission);
            if (_footer != null) _footer.text = " ";
            if (_gold != null) _gold.style.display = DisplayStyle.None;

            WriteFindings(findings);

            VisualElement sheet = root.Q<VisualElement>("proc-sheet");
            if (sheet?.parent != null) _scroll = new ProclamationScroll(sheet);
        }

        /// <summary>Unfurl, then stamp each finding. <paramref name="onOpened"/> runs once the
        /// sheet has stopped, before the stamps land.</summary>
        public void Open(Action onOpened = null)
        {
            if (_scroll == null) { StampFindings(); onOpened?.Invoke(); return; }
            _scroll.Open(() =>
            {
                StampFindings();
                onOpened?.Invoke();
            });
        }

        /// <summary>Roll shut and run <paramref name="onClosed"/>.</summary>
        public void Close(Action onClosed)
        {
            SealStamp.Clear(_root);
            if (_scroll == null) { onClosed?.Invoke(); return; }
            _scroll.Close(onClosed);
        }

        /// <summary>True while the scroll is moving, when a tap must not count.</summary>
        public bool IsMoving => _scroll?.IsMoving == true;

        /// <summary>Write the spoils: the Gold this match paid and the quests it moved. Zero Gold and
        /// no quests leaves the column hidden.</summary>
        public void ShowSpoils(long gold, IReadOnlyList<QuestAdvance> quests)
        {
            bool any = false;
            if (_gold != null && gold > 0)
            {
                _gold.text = Loc.Format("match_result_gold_earned", gold);
                _gold.style.display = DisplayStyle.Flex;
                any = true;
            }
            if (_quests != null && quests != null)
            {
                foreach (QuestAdvance advance in quests)
                {
                    QuestEntry quest = advance.Quest;
                    string detail = advance.CompletedNow
                        ? Loc.Format("match_result_quest_complete", quest.RewardGold)
                        : Loc.Format("match_result_quest_progress", quest.Progress, quest.Goal,
                                     quest.Progress - advance.PreviousProgress);
                    VisualElement row = Row(quest.Title, detail);
                    if (advance.CompletedNow) row.ElementAt(1).AddToClassList("proc-row__detail--done");
                    _quests.Add(row);
                    any = true;
                }
            }
            if (any) _spoils?.RemoveFromClassList(HiddenColumnClass);
        }

        public void SetFooter(string text)
        {
            if (_footer != null) _footer.text = string.IsNullOrEmpty(text) ? " " : text;
        }

        /// <summary>Show Play Again and run <paramref name="onPress"/> when it is pressed. Until
        /// this is called the row is hidden, and the screen offers only the tap to deck selection.</summary>
        public void ShowPlayAgain(Action onPress)
        {
            if (_playAgain == null) return;
            _playAgain.text = Loc.Get("match_result_play_again");
            _playAgain.clicked += () => onPress?.Invoke();
            _actions?.RemoveFromClassList(HiddenActionsClass);
        }

        /// <summary>
        /// True when <paramref name="screenPosition"/> (Input System pixels, origin bottom-left) is
        /// on Play Again. The sequence's tap-anywhere reads the pointer, not UI events, so it has to
        /// be told to leave a press on the button to the button — otherwise the press leaves for
        /// deck selection a frame before the button's click arrives.
        /// </summary>
        public bool IsOverPlayAgain(Vector2 screenPosition)
        {
            if (_playAgain?.panel == null || _actions == null || _actions.ClassListContains(HiddenActionsClass))
                return false;
            Vector2 panelPosition = RuntimePanelUtils.ScreenToPanel(
                _playAgain.panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
            return _playAgain.worldBound.Contains(panelPosition);
        }

        private void WriteFindings(IReadOnlyList<string> ids)
        {
            if (_findings == null || ids == null) return;
            int known = 0;
            foreach (string id in ids)
            {
                AchievementText text = AchievementTexts.Find(id);
                if (text == null) continue;
                known++;
                if (known > MaxFindingsShown) continue;
                VisualElement row = Row(text.Name, text.Citation);
                row.AddToClassList("proc-finding");
                _findings.Add(row);
                _findingRows.Add(row);
            }
            if (known > MaxFindingsShown)
            {
                int more = known - MaxFindingsShown;
                var label = new Label(Loc.Plural("match_result_findings_more", more, more));
                label.AddToClassList("proc-more");
                _findings.Add(label);
            }
            if (known > 0) _findingsColumn?.RemoveFromClassList(HiddenColumnClass);
        }

        /// <summary>
        /// The King's line, under the reason: the commission, this match's score, and <i>New best</i>
        /// when it beat the player's best this reign (spec, "In the match"). This is where a win-only
        /// score becomes real. The server decided it counts; the client only words it, and a
        /// commission this build has no name for is left out rather than printed by id.
        /// </summary>
        private static void WriteCommission(Label line, CommissionFinal commission)
        {
            if (line == null || commission == null) return;
            string name = KingText.Commission(commission.Id, "name");
            if (name == null) return;
            // Two literal calls, not one with a ternary key, so LocalizationVerify sees both.
            line.text = commission.NewBest
                ? Loc.Format("king_proc_commission_new_best", name, commission.Score)
                : Loc.Format("king_proc_commission", name, commission.Score);
            line.RemoveFromClassList("proc-commission--hidden");
        }

        private void StampFindings()
        {
            Sprite seal = _sprites?.Get(SealStamp.SpriteKey);
            if (seal == null) return;
            Sprite tool = _sprites.Get(SealStamp.ToolSpriteKey);
            for (int i = 0; i < _findingRows.Count; i++)
            {
                VisualElement row = _findingRows[i];
                row.schedule.Execute(() =>
                {
                    // The player may have left, rolling the sheet up, inside the delay.
                    if (IsMoving || row.panel == null) return;
                    SealStamp.Play(row, seal, tool, size: SealSize, right: 0f, top: -4f);
                }).StartingIn(StampDelayMs + i * StampGapMs);
            }
        }

        private static VisualElement Row(string title, string detail)
        {
            var row = new VisualElement();
            row.AddToClassList("proc-row");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("proc-row__title");
            var detailLabel = new Label(detail);
            detailLabel.AddToClassList("proc-row__detail");
            row.Add(titleLabel);
            row.Add(detailLabel);
            return row;
        }

        /// <summary>InProgress is the connection ending before the match did — a reconnect that
        /// never got through, or one the server refused because the match was already over. The
        /// client does not know the result then, so it does not claim one. It used to fall through
        /// to DEFEAT, which is how a three-second Wi-Fi blip read as a loss (2026-11-04). Drawn in
        /// the draw's colours: neither side's, and no new style to keep.</summary>
        private static (string, string) Judge(MatchOutcome outcome, int localSeat) => outcome switch
        {
            MatchOutcome.Player0Win when localSeat == 0 => (Loc.Get("match_result_verdict_victory"), "proc-verdict--victory"),
            MatchOutcome.Player1Win when localSeat == 1 => (Loc.Get("match_result_verdict_victory"), "proc-verdict--victory"),
            MatchOutcome.Draw                           => (Loc.Get("match_result_verdict_draw"), "proc-verdict--draw"),
            MatchOutcome.InProgress                     => (Loc.Get("match_result_verdict_disconnected"), "proc-verdict--draw"),
            _                                           => (Loc.Get("match_result_verdict_defeat"), "proc-verdict--defeat"),
        };

        /// <summary>The line under the verdict: why it ended, in the Council's voice (whimsy pass
        /// 2026-10-17). <paramref name="reason"/> is null when the socket died before the server's
        /// <c>MatchEndMsg</c> arrived, and that falls back to the plain win or loss line.</summary>
        private static string ResultDetail(MatchOutcome outcome, int localSeat, MatchEndReason? reason)
        {
            if (outcome == MatchOutcome.Draw) return Loc.Get("match_result_reason_draw");
            if (outcome == MatchOutcome.InProgress) return Loc.Get("match_result_reason_disconnected");

            bool localWon = outcome == MatchOutcome.Player0Win && localSeat == 0
                            || outcome == MatchOutcome.Player1Win && localSeat == 1;
            if (!localWon) return Loc.Get("match_result_reason_defeat");

            return reason switch
            {
                MatchEndReason.Concede => Loc.Get("match_result_reason_opponent_conceded"),
                // Two is ServerOptions.IdleForfeitAutoTurns, which the wire does not carry.
                MatchEndReason.Timeout => Loc.Get("match_result_reason_opponent_timeout"),
                _ => Loc.Get("match_result_reason_victory"),
            };
        }
    }
}
