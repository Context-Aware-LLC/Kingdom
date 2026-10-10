using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Plays the LOCAL hero's voice lines at match and turn moments. Presentation-only: it reads
    /// the same paced snapshots, event batches and turn timers every other view reads, and never
    /// feeds anything back into authority state.
    /// </summary>
    /// <remarks>
    /// <para><b>Keyed on the hero's CLASS, not on the character.</b> A character is a
    /// <c>PrimaryClass</c>/<c>SecondaryClass</c> pairing and the match hero is always the primary
    /// (dual-class ruling), so eight voices cover every character that can ever exist. The stem is
    /// <c>vo_&lt;class&gt;_&lt;moment&gt;_&lt;n&gt;</c> with the class lowercased, matching
    /// <c>tooling/sfx/voice-lines.json</c> and the files under <c>Assets/Sound/Voice/</c>.</para>
    ///
    /// <para><b>All eight classes are recorded and addressed</b> (2026-08-28, 120 clips), so a key
    /// that does not resolve is now a real fault and warns. It did not always: while seven classes
    /// were silent the player ran with <c>logMissing: false</c>, because an unrecorded class must
    /// be quiet rather than fill the console every turn. That trigger is discharged; do not put
    /// the flag back without also recording why a whole class is expected to be missing.</para>
    ///
    /// <para><b>There is no MatchStarted event</b> — <see cref="GameEventKind"/> has no such member.
    /// The first <see cref="GameEventKind.TurnStarted"/> of the match stands in for it, latched, and
    /// that turn deliberately does not also get a turn_start line.</para>
    /// </remarks>
    public sealed class HeroVoiceDirector : MonoBehaviour
    {
        /// <summary>Takes authored per moment in voice-lines.json (vo_..._1 … vo_..._3).</summary>
        public const int VariantsPerMoment = 3;

        /// <summary>
        /// Sam's ruling (2026-08-28): the turn_start line is a flourish, not a turn marker — the
        /// clock already marks the turn. Fire it occasionally, at a low rate, or a line the player
        /// hears every single turn stops being a flourish and becomes noise.
        /// </summary>
        public const float TurnStartChance = 0.25f;

        /// <summary>
        /// Client-side rope window, deliberately owned here rather than read off the server's
        /// <c>ServerOptions.RopeMs</c>: the line should land when the player SEES the clock go red,
        /// which <see cref="TurnClockView"/> does on its own copy of this number. All three are 15s
        /// (Sam, 2026-10-10 — the two client ones were 10s, against a published 15); move them
        /// together. The server's rope broadcast is a
        /// second <c>TurnTimerMsg</c> carrying the SAME deadline as the arm, so it cannot be told
        /// apart from a reconnect resend by content — the countdown is the reliable signal.
        /// </summary>
        private const long RopeWindowMs = 15_000;

        private const string MomentMatchStart = "match_start";
        private const string MomentTurnStart = "turn_start";
        private const string MomentRope = "rope";
        private const string MomentWin = "win";
        private const string MomentLose = "lose";

        private AddressableSoundEffectPlayer _player;
        private string _classToken;
        private PlayerId _localPlayer;
        private int _localSeat;
        private Func<long> _nowMs;

        private InstanceId? _localHeroId;
        private long? _ropeDeadlineMs;   // null unless a LOCAL turn's deadline is still pending
        private bool _matchStartFired;
        private bool _ended;
        private string _matchEndSoundId; // null unless a win/lose line is picked and not yet played

        /// <param name="heroClass">The local hero's class — <c>NEUTRAL</c> disables the director,
        /// since NEUTRAL is not pairable and no hero can be one.</param>
        /// <param name="nowMs">Unix-epoch millisecond source; defaults to the wall clock. Injectable
        /// for the same reason <see cref="TurnClockView"/> injects it.</param>
        public void Init(CardClass heroClass, PlayerId localPlayer, int localSeat, Func<long> nowMs = null)
        {
            _localPlayer = localPlayer;
            _localSeat = localSeat;
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _classToken = heroClass == CardClass.NEUTRAL
                ? null
                : heroClass.ToString().ToLowerInvariant();
            if (_classToken == null) return;

            // TryGetComponent, not `??` — see CardSoundDirector.Init for why the null-coalescing
            // operator is the wrong null check against a UnityEngine.Object.
            if (!TryGetComponent(out AudioSource source)) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = AudioSettings.SfxGroup;
            _player = new AddressableSoundEffectPlayer(
                source,
                AddressableSoundEffectPlayer.HeroVoicePrefix,
                owner: nameof(HeroVoiceDirector));
        }

        public void OnStateUpdated(GameState state)
        {
            if (_player == null || state == null) return;

            // The hero InstanceId is minted once and never moves, so one capture serves the match.
            // Reading it off state rather than off the event stream keeps the TurnStarted handler
            // independent of whether the snapshot or the batch is delivered first for a beat.
            if (_localHeroId == null && state.Players != null)
                foreach (PlayerState p in state.Players)
                    if (p != null && p.Id == _localPlayer && p.Hero != null)
                        _localHeroId = p.Hero.Id;

            if (state.Phase == TurnPhase.Mulligan || state.Outcome != MatchOutcome.InProgress)
                _ropeDeadlineMs = null;
        }

        public void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            if (_player == null || batch == null || _ended) return;
            foreach (GameEvent matchEvent in batch)
            {
                if (matchEvent.Kind != GameEventKind.TurnStarted) continue;
                if (!_matchStartFired)
                {
                    _matchStartFired = true;
                    Play(MomentMatchStart);
                    continue;   // the opening turn gets the match line, never both
                }

                if (_localHeroId.HasValue && matchEvent.Source.HasValue
                    && matchEvent.Source.Value == _localHeroId.Value
                    && UnityEngine.Random.value < TurnStartChance)
                    Play(MomentTurnStart);
            }
        }

        /// <summary>Bind to <see cref="IMatchSession.OnTurnTimer"/> — already main-thread-marshaled
        /// by the networked decorator, as <see cref="TurnClockView.OnTurnTimer"/> relies on too.</summary>
        public void OnTurnTimer(TurnTimer t)
        {
            if (_player == null || t == null || _ended) return;
            // Only the player under the clock is being roped. The arm, the server's rope broadcast
            // and a reconnect resend all carry the same deadline, so re-arming on each is a no-op
            // once the line has fired for that deadline (_ropeDeadlineMs is cleared, not re-set).
            if (t.ActivePlayer != _localPlayer) { _ropeDeadlineMs = null; return; }
            if (_ropeDeadlineMs == t.DeadlineMs) return;
            if (t.DeadlineMs - _nowMs() <= RopeWindowMs) return;   // already inside the window: too late to be a warning
            _ropeDeadlineMs = t.DeadlineMs;
        }

        public void OnMatchEnded(MatchOutcome outcome)
        {
            if (_player == null || _ended) return;
            _ended = true;
            _ropeDeadlineMs = null;
            // Draw is deliberately silent — no line is authored for it (voice-lines.json _moments).
            if (outcome == MatchOutcome.Draw || outcome == MatchOutcome.InProgress) return;
            bool won = outcome == (_localSeat == 0 ? MatchOutcome.Player0Win : MatchOutcome.Player1Win);
            _matchEndSoundId = SoundId(won ? MomentWin : MomentLose);
            _player.Preload(_matchEndSoundId);   // so the line lands on the beat, not a load after it
        }

        /// <summary>
        /// Plays the win/lose line <see cref="OnMatchEnded"/> picked. The timing is not this
        /// class's to decide: <see cref="MatchEndSequence"/> calls it once the stinger's music is
        /// over and the quest cards have had their beat (Sam, 2026-10-03 — the line and the
        /// stinger played on top of each other when both fired on the match-end event).
        /// </summary>
        public void PlayMatchEndLine()
        {
            if (_player == null || _matchEndSoundId == null) return;
            _player.Play(_matchEndSoundId);
            _matchEndSoundId = null;
        }

        private void Update()
        {
            if (_ropeDeadlineMs == null) return;
            if (_ropeDeadlineMs.Value - _nowMs() > RopeWindowMs) return;
            _ropeDeadlineMs = null;
            Play(MomentRope);
        }

        private void Play(string moment) => _player.Play(SoundId(moment));

        private string SoundId(string moment)
        {
            int variant = UnityEngine.Random.Range(1, VariantsPerMoment + 1);
            return $"vo_{_classToken}_{moment}_{variant}";
        }

        private void OnDestroy() => _player?.ReleaseAll();
    }
}
