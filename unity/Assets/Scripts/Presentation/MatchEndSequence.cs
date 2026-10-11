using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Net.Client.Quests;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Paces the result screen and then returns the player to deck selection (Sam, 2026-10-03):
    /// the stinger's music, then the quests this match moved, a short pause, the hero's win/lose
    /// line, and a long linger the player can end with a tap. It was a flat three-second return
    /// before, which left for the picker before the music had finished and before the voice line
    /// was due at all.
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

        /// <summary>How much longer than the music a slow quest read is waited for before the
        /// screen goes on without it.</summary>
        public const float QuestWaitSeconds = 2f;

        /// <summary>The pause between the quest cards landing and the voice line.</summary>
        public const float QuestBeatSeconds = 1.5f;

        /// <summary>How long the finished screen stays up if the player does nothing.</summary>
        public const float LingerSeconds = 20f;

        private Task<IReadOnlyList<QuestAdvance>> _quests;
        private Action _playVoiceLine;
        private TMP_Text _footer;
        private bool _canLeave;
        private bool _left;

        public void Init(Task<IReadOnlyList<QuestAdvance>> quests, Action playVoiceLine, TMP_Text footer)
        {
            _quests = quests;
            _playVoiceLine = playVoiceLine;
            _footer = footer;
        }

        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(StingerSeconds);

            for (float waited = 0f;
                 _quests != null && !_quests.IsCompleted && waited < QuestWaitSeconds;
                 waited += Time.unscaledDeltaTime)
                yield return null;

            if (_quests != null && _quests.Status == TaskStatus.RanToCompletion && _quests.Result.Count > 0)
            {
                MatchEndBanner.ShowQuests(transform, _quests.Result);
                yield return new WaitForSecondsRealtime(QuestBeatSeconds);
            }

            _playVoiceLine?.Invoke();

            // Only now: a tap still in flight from the last play of the match must not skip the
            // screen it has not seen yet.
            _canLeave = true;
            if (_footer != null) _footer.text = "Tap to continue";

            yield return new WaitForSecondsRealtime(LingerSeconds);
            Leave();
        }

        private void Update()
        {
            if (_canLeave && Pointer.current?.press.wasPressedThisFrame == true) Leave();
        }

        private void Leave()
        {
            if (_left) return;
            _left = true;
            MatchDeckSelection.OpenPicker();
        }
    }
}
