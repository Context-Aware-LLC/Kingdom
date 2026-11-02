using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Net.Client.Quests;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Paces the result screen and then returns the player to deck selection (Sam, 2026-10-03):
    /// the proclamation unrolls with the verdict and the findings while the stinger's music plays,
    /// then the spoils this match paid (Gold and quests), a short pause, the hero's win/lose line,
    /// and a long linger the player can end with a tap. On leaving, the scroll rolls shut first.
    ///
    /// <para><b>Play Again</b> (launch item 6) appears with the tap prompt after a queued match:
    /// one press queues the same deck again and loads the board, skipping the roster
    /// (<see cref="MatchDeckSelection.Rematch"/>, funnel source <c>result_view</c>). A tap anywhere
    /// else, or the linger running out, still goes to deck selection, which is how a player changes
    /// decks. A friend challenge gets no Play Again: its ticket is spent, and queueing the deck would
    /// seat the player against a stranger rather than the friend.</para>
    ///
    /// <para>Nothing here holds a server resource: the server closes both sockets with the
    /// <c>MatchEndMsg</c> (<c>MatchSession.EndMatch</c>), so the linger costs a connection
    /// nothing however long it runs.</para>
    ///
    /// <para>Every wait is realtime — the screen must not hang on a paused or slowed
    /// timeScale.</para>
    /// </summary>
    public sealed class MatchEndSequence : MonoBehaviour
    {
        /// <summary>
        /// How long the stinger's MUSIC runs (<see cref="CardSoundDirector.OnMatchEnded"/> starts
        /// it on the same event). The stingers are 6.48s files whose music is over well before
        /// the file is — the last window above -30 dB of peak falls between 4.15s and 5.0s across
        /// the five — so this is the end of the music, not the length of the clip. Re-measure if
        /// a stinger is replaced.
        /// </summary>
        public const float StingerSeconds = 4.5f;

        /// <summary>How much longer than the music a slow spoils read is waited for before the
        /// screen goes on without it.</summary>
        public const float SpoilsWaitSeconds = 2f;

        /// <summary>The pause between the spoils landing and the voice line.</summary>
        public const float SpoilsBeatSeconds = 1.5f;

        /// <summary>How long the finished screen stays up if the player does nothing.</summary>
        public const float LingerSeconds = 20f;

        /// <summary>The longest the scroll is waited on to roll shut before the screen leaves anyway.</summary>
        private const float FurlWaitSeconds = 1f;

        private MatchProclamation _proclamation;
        private Task<IReadOnlyList<QuestAdvance>> _quests;
        private Task<long> _gold;
        private Action _playVoiceLine;
        private string _rematchDeckId;
        private bool _canLeave;
        private bool _left;

        public void Init(MatchProclamation proclamation, Task<IReadOnlyList<QuestAdvance>> quests,
                         Task<long> gold, Action playVoiceLine, string rematchDeckId = null)
        {
            _rematchDeckId = rematchDeckId;
            _proclamation = proclamation;
            _quests = quests;
            _gold = gold;
            _playVoiceLine = playVoiceLine;
        }

        private IEnumerator Start()
        {
            _proclamation?.Open();
            yield return new WaitForSecondsRealtime(StingerSeconds);

            for (float waited = 0f; !Settled(_quests) || !Settled(_gold); waited += Time.unscaledDeltaTime)
            {
                if (waited >= SpoilsWaitSeconds) break;
                yield return null;
            }

            long gold = _gold != null && _gold.Status == TaskStatus.RanToCompletion ? _gold.Result : 0;
            IReadOnlyList<QuestAdvance> quests =
                _quests != null && _quests.Status == TaskStatus.RanToCompletion ? _quests.Result : null;
            if (gold > 0 || quests?.Count > 0)
            {
                _proclamation?.ShowSpoils(gold, quests);
                yield return new WaitForSecondsRealtime(SpoilsBeatSeconds);
            }

            _playVoiceLine?.Invoke();

            // Only now: a tap still in flight from the last play of the match must not skip the
            // screen it has not seen yet.
            _canLeave = true;
            if (CanPlayAgain)
            {
                _proclamation.ShowPlayAgain(PlayAgain);
                _proclamation.SetFooter(Loc.Get("match_result_tap_to_change_deck"));
            }
            else
            {
                _proclamation?.SetFooter(Loc.Get("match_result_tap_to_continue"));
            }

            yield return new WaitForSecondsRealtime(LingerSeconds);
            Leave();
        }

        private static bool Settled(Task task) => task == null || task.IsCompleted;

        private bool CanPlayAgain => _proclamation != null && !string.IsNullOrEmpty(_rematchDeckId);

        private void Update()
        {
            Pointer pointer = Pointer.current;
            if (!_canLeave || _proclamation?.IsMoving == true || pointer?.press.wasPressedThisFrame != true) return;
            // A press on Play Again is the button's: its click arrives on release.
            if (CanPlayAgain && _proclamation.IsOverPlayAgain(pointer.position.ReadValue())) return;
            Leave();
        }

        private void Leave()
        {
            if (_left) return;
            _left = true;
            StartCoroutine(LeaveAfterFurl(() => MatchDeckSelection.OpenPicker()));
        }

        private void PlayAgain()
        {
            if (_left || _proclamation?.IsMoving == true) return;
            _left = true;
            string deckId = _rematchDeckId;
            StartCoroutine(LeaveAfterFurl(() => MatchDeckSelection.Rematch(deckId)));
        }

        /// <summary>Roll the sheet up, then go. Bounded: a furl that never reports back (its panel
        /// gone, a layout that never resolves) must not keep the player on a finished match.</summary>
        private IEnumerator LeaveAfterFurl(Action go)
        {
            bool closed = _proclamation == null;
            _proclamation?.Close(() => closed = true);
            for (float waited = 0f; !closed && waited < FurlWaitSeconds; waited += Time.unscaledDeltaTime)
                yield return null;
            go();
        }
    }
}
