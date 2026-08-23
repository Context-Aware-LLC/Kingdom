using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.GoldenReplay;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.MigrationParity
{
    /// <summary>
    /// Records a <see cref="BehavioralTrace"/> from the current engine by driving a replay fixture
    /// through <see cref="LocalMatch"/>'s per-viewer session pair.
    ///
    /// <para><b>Why the session and not the host.</b> <c>LocalMatchHost.State</c> is the AUTHORITY
    /// surface. A behavioral trace is what a player can observe, so it is recorded off
    /// <see cref="IMatchSession"/>, whose snapshots and event batches are already post-redaction.
    /// That is also what makes the trace's identity content meaningful: a redacted id arrives as the
    /// reserved sentinel, and the comparator must not renumber it into somebody real.</para>
    ///
    /// <para><b>THE COMPARED SURFACE — the exclusions are the spec.</b> Per command this records:
    /// the canonical command rendering and its identities; accept/reject with reason; the ordered
    /// player-visible outcomes with their identities; the pending choice and its legal option set;
    /// and the projected state. Plus the terminal outcome. Anything not in that list is NOT checked
    /// by the gate. Known exclusions, deliberate: raw event rows and internal hashes (the redesign
    /// changes both, so comparing them would pin the implementation being replaced); the opponent's
    /// view (parity is asserted per viewer, and this records seat <see cref="ViewerSeat"/>); and
    /// wall-clock/turn-timer state.</para>
    /// </summary>
    public static class TraceRecorder
    {
        /// <summary>The seat whose view is recorded. Parity is asserted per viewer.</summary>
        public const int ViewerSeat = 0;

        public static BehavioralTrace Record(ReplayFixture fx)
        {
            if (fx is null) throw new ArgumentNullException(nameof(fx));

            var db = JsonCardDatabase.FromSnapshotFile(Path.Combine(RepoPaths.CardSetsDir, fx.CardSetSnapshot));
            var setup = new MatchSetup(fx.P0Class, fx.P0Deck, fx.P1Class, fx.P1Deck,
                heroMaxHealth: fx.HeroMaxHealth, sparkTokenId: fx.SparkTokenId);

            var (s0, s1) = LocalMatch.Start(db, setup, fx.P0, fx.P1, fx.Seed, fx.MatchId);
            var viewer = ViewerSeat == 0 ? s0 : s1;

            var batch = new List<GameEvent>();
            viewer.OnEvents += evts => batch.AddRange(evts);

            var trace = new BehavioralTrace { Name = fx.Name };

            // The opening is core-driven, so the state before any intent is part of the trace.
            var opening = new TraceStep { Label = "BeginMatch" };
            opening.Tokens.Add(new TraceToken("command", TokenKind.Scalar, "BeginMatch"));
            opening.Tokens.Add(new TraceToken("accepted", TokenKind.Scalar, "true"));
            AppendChoice(opening.Tokens, viewer.Current);
            AppendState(opening.Tokens, viewer.Current);
            trace.Steps.Add(opening);

            foreach (var intent in fx.Intents)
            {
                batch.Clear();
                var session = intent.Player == fx.P0 ? s0 : s1;
                var result = session.SubmitAsync(intent).GetAwaiter().GetResult();

                var step = new TraceStep { Label = RenderCommand(intent) };
                step.Tokens.Add(new TraceToken("command", TokenKind.Scalar, RenderCommand(intent)));
                step.Tokens.Add(new TraceToken("player", TokenKind.Scalar, intent.Player.Value));

                int k = 0;
                foreach (var id in CommandIds(intent))
                    step.Tokens.Add(IdToken("command.id[" + (k++).ToString(CultureInfo.InvariantCulture) + "]", id));

                step.Tokens.Add(new TraceToken("accepted", TokenKind.Scalar, result.Accepted ? "true" : "false"));
                step.Tokens.Add(new TraceToken("rejectReason", TokenKind.Scalar, result.Reason?.ToString() ?? "none"));

                step.Tokens.Add(new TraceToken("outcome.Count", TokenKind.Scalar,
                    batch.Count.ToString(CultureInfo.InvariantCulture)));
                for (int i = 0; i < batch.Count; i++)
                {
                    var p = "outcome[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    step.Tokens.Add(new TraceToken(p, TokenKind.Scalar, RenderOutcome(batch[i])));
                    step.Tokens.Add(IdToken(p + ".src", batch[i].Source));
                    step.Tokens.Add(IdToken(p + ".tgt", batch[i].Target));
                }

                AppendChoice(step.Tokens, viewer.Current);
                AppendState(step.Tokens, viewer.Current);
                trace.Steps.Add(step);
            }

            trace.TerminalOutcome = viewer.Current.Outcome.ToString();
            return trace;
        }

        private static void AppendState(List<TraceToken> tokens, GameState s)
        {
            foreach (var t in StateTokenizer.Tokenize(s))
                tokens.Add(new TraceToken("state." + t.Path, t.Kind, t.Value));
        }

        private static void AppendChoice(List<TraceToken> tokens, GameState s)
        {
            if (s.PendingChoice is null)
            {
                tokens.Add(new TraceToken("choice", TokenKind.Scalar, "none"));
                return;
            }
            tokens.Add(new TraceToken("choice", TokenKind.Scalar, "pending"));
            tokens.Add(new TraceToken("choice.chooser", TokenKind.Scalar, s.PendingChoice.Chooser.Value));
            tokens.Add(new TraceToken("choice.kind", TokenKind.Scalar, s.PendingChoice.Kind.ToString()));
            tokens.Add(new TraceToken("choice.option.Count", TokenKind.Scalar,
                s.PendingChoice.Options.Count.ToString(CultureInfo.InvariantCulture)));
            for (int i = 0; i < s.PendingChoice.Options.Count; i++)
                tokens.Add(new TraceToken("choice.option[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                    TokenKind.Scalar, s.PendingChoice.Options[i]));
        }

        private static TraceToken IdToken(string path, InstanceId? id)
        {
            if (!id.HasValue) return new TraceToken(path, TokenKind.Id, StateTokenizer.AbsentId);
            if (id.Value.Value == 0) return new TraceToken(path, TokenKind.Id, StateTokenizer.RedactedId);
            return new TraceToken(path, TokenKind.Id, id.Value.Value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Canonical command rendering. Identities are kept OUT of the string and emitted as id
        /// tokens, so a command naming card i7 on one engine and i9 on the other is a renumbering
        /// rather than a different command.
        /// </summary>
        private static string RenderCommand(PlayerIntent intent) => intent switch
        {
            PlayCardIntent p => "PlayCard slot=" + p.BoardSlot.ToString(CultureInfo.InvariantCulture),
            AttackIntent => "Attack",
            HeroPowerIntent => "HeroPower",
            EndTurnIntent => "EndTurn",
            MulliganIntent m => "Mulligan n=" + m.ToReplace.Count.ToString(CultureInfo.InvariantCulture),
            UnearthChoiceIntent u => "UnearthChoice card=" + u.ChosenCardId + " seq=" + u.ChoiceSequence.ToString(CultureInfo.InvariantCulture),
            ConcedeIntent => "Concede",
            AutoConfirmIntent => "AutoConfirm",
            _ => throw new NotSupportedException(
                $"No canonical rendering for {intent.GetType().Name} — an unrendered intent is a command the gate cannot compare."),
        };

        /// <summary>Identities inside an intent, in declaration order, so they canonicalize.</summary>
        private static IEnumerable<InstanceId?> CommandIds(PlayerIntent intent)
        {
            switch (intent)
            {
                case PlayCardIntent p:
                    yield return p.Card;
                    foreach (var t in p.Targets) yield return t.Instance;
                    break;
                case AttackIntent a:
                    yield return a.Attacker;
                    yield return a.Defender.Instance;
                    break;
                case HeroPowerIntent h:
                    foreach (var t in h.Targets) yield return t.Instance;
                    break;
                case MulliganIntent m:
                    foreach (var i in m.ToReplace) yield return i;
                    break;
            }
        }

        /// <summary>
        /// A player-visible outcome, NOT a raw event row: semantic kind, flags and amount, with the
        /// identities carried separately so they canonicalize.
        /// </summary>
        private static string RenderOutcome(GameEvent e)
            => e.Kind.ToString()
             + " flags=" + e.Flags.ToString()
             + " amt=" + (e.Amount.HasValue ? e.Amount.Value.ToString(CultureInfo.InvariantCulture) : "none");
    }
}
