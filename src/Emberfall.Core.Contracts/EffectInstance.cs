using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// EXPERIMENT (effect-model fork, 2026-08-14). The flat, extensible id space for "something is
    /// true about this entity for a while." Deliberately NOT partitioned into families — a new
    /// member is one line and attaches wherever it fits (Sam, 2026-08-14).
    ///
    /// <para>The one bit membership does carry is <b>granted vs derived</b>, expressed by
    /// <see cref="EffectKindFacts.IsGrantable"/>. A derived member (where the card is, what has
    /// happened to it) is never applied by anything, so "apply inGraveyard to this minion" must not
    /// parse. That is a flag, not a taxonomy, and it costs the flat space nothing.</para>
    /// </summary>
    public enum EffectKind
    {
        // RETIREMENT TRIGGER: adding a second member here ends this enum's carve-out in
        // WireMutationSweepTests.Mutated_Field_Survives_The_Wire. While only one member exists the
        // "EffectInstance.Kind" mutation can only cast past it, so the wire's correct answer is a
        // typed refusal rather than a round-trip, and the sweep asserts the refusal. With a second
        // member the mutation becomes a real round-trip: drop "EffectInstance.Kind" from that
        // branch in the same commit, or the sweep will demand a failure the wire no longer produces.
        /// <summary>Cannot act. Migrated off <c>IsFrosted</c> + <c>FrostThawPending</c>.</summary>
        Frost = 0,
    }

    /// <summary>
    /// How an <see cref="EffectInstance"/> measures its own lifetime. <see cref="ControllerTurns"/>
    /// counts <see cref="EffectInstance.TurnsRemaining"/> down at the START of the controller's turn
    /// and expires the instance at the END of a controller turn it entered at zero.
    ///
    /// <para><b>That start-decrement/end-expire split is the whole point</b>, and it is what deleted
    /// <c>FrostThawPending</c>. An effect applied during its own controller's turn has not been
    /// decremented yet when that turn ends, so it survives to cost a FULL turn rather than the
    /// remainder of one — the NOR-367 warden case, previously hand-encoded in a second boolean.</para>
    /// </summary>
    public enum EffectScope
    {
        /// <summary>Never ticks. Removed only by something that removes it.</summary>
        Permanent = 0,
        ControllerTurns = 1,
    }

    /// <summary>
    /// EXPERIMENT (effect-model fork). One applied effect, addressable and mutable.
    ///
    /// <para><b>No identity yet, deliberately.</b> The thesis needs one — you cannot mutate or target
    /// what you cannot name, and <c>Enchantment</c>'s lack of an id is why nothing can target it. But
    /// an unminted <c>InstanceId</c> defaults to <c>InstanceId(0)</c>, which is the reserved REDACTION
    /// SENTINEL (<c>GameStateProjector.Sentinel</c>; the counter starts at 1, so hero0's live id is 1 —
    /// asserted by <c>SentinelReservationTests.Engine_Never_Mints_InstanceId_Zero</c>). Shipping the
    /// field before the counter that fills it would therefore alias "no identity" onto "hidden from
    /// you" rather than onto a live entity. Backend-first (Sam, 2026-08-03): the id lands in the cut
    /// that makes effects targetable, together with its minting path.
    /// <b>Corrected 2026-08-15 (eng review):</b> this comment previously said zero was hero0's live id,
    /// which is false; the conclusion stands but the reason is the sentinel collision below.</para>
    ///
    /// <para><b>FIXED (2026-08-16, Phase 0.5 item 1).</b> <see cref="Source"/> is <c>InstanceId?</c>.
    /// <c>null</c> means "nothing applied this" (absence); a real value means who applied it;
    /// <c>GameStateProjector.Sentinel</c> (<c>InstanceId(0)</c>) is still emitted by
    /// <c>GameStateProjector.CloneEffects</c> for "applied by something you cannot see", but that is
    /// now a distinct wire value from absence — a client can tell the two apart. Was previously latent
    /// (both cases wrote the same <c>InstanceId(0)</c>) because every real call site passed a non-null
    /// source; now fixed ahead of the first caller that needs to pass none.</para>
    /// </summary>
    public sealed class EffectInstance
    {
        public EffectKind Kind;
        /// <summary>Who applied it. <c>null</c> when nothing did (a derived member).</summary>
        public InstanceId? Source;
        public EffectScope Scope;
        /// <summary>Meaningless unless <see cref="Scope"/> is <see cref="EffectScope.ControllerTurns"/>.</summary>
        public int TurnsRemaining;

        public EffectInstance() { }

        public EffectInstance(EffectKind kind, InstanceId? source, EffectScope scope, int turnsRemaining)
        {
            Kind = kind;
            Source = source;
            Scope = scope;
            TurnsRemaining = turnsRemaining;
        }
    }

    /// <summary>The one bit of membership metadata the flat id space carries. See <see cref="EffectKind"/>.</summary>
    public static class EffectKindFacts
    {
        public static bool IsGrantable(EffectKind kind)
        {
            switch (kind)
            {
                case EffectKind.Frost: return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// The tick and query surface shared by every effect host. Hosts hold a plain
    /// <c>List&lt;EffectInstance&gt;</c>; ordering is application order and is therefore already a
    /// total order, so nothing needs to sort it to stay deterministic.
    ///
    /// <para><b>Invariant: at most one <see cref="EffectInstance"/> per <see cref="EffectKind"/> per
    /// host.</b> <see cref="Apply"/> is the sole insertion point — it finds the existing instance of
    /// the same <see cref="EffectKind"/> and refreshes it in place, or appends if none exists — and
    /// nothing else in the tree mutates a host's <c>Effects</c> list (<c>git grep -n
    /// "\.Effects\.Add(\|\.Effects\.Insert(\|\.Effects\.AddRange(" -- src</c> returns nothing, checked
    /// 2026-08-16). So this is a SET keyed by <see cref="EffectKind"/> wearing a list's clothes, and
    /// every reader below may assume 0-or-1 matches rather than fold over several. The cost of that
    /// shape: two different sources applying the same kind collapse into one record, and the
    /// non-winning apply's <see cref="EffectInstance.Source"/> is overwritten or retained depending on
    /// which one already held the slot — provenance loss, not tracked as its own Phase 0.5 item and
    /// not what this invariant promises to fix. (Not the same concern as absence-vs-redaction
    /// encoding, which Phase 0.5 item 1 does fix.)</para>
    /// </summary>
    public static class EffectList
    {
        public static bool Has(List<EffectInstance> effects, EffectKind kind)
        {
            for (int i = 0; i < effects.Count; i++)
                if (effects[i].Kind == kind) return true;
            return false;
        }

        /// <summary>
        /// Applies, or refreshes an existing instance of the same kind to the longer remaining life.
        ///
        /// <para><b><see cref="EffectScope.Permanent"/> is a ceiling, not a number.</b> An existing
        /// Permanent instance is never downgraded by an incoming <see cref="EffectScope.ControllerTurns"/>
        /// — comparing <see cref="EffectInstance.TurnsRemaining"/> numerically against it would do
        /// exactly that, because the field is meaningless on a Permanent instance (see its doc comment)
        /// and can easily compare less than an incoming apply's turn count. An incoming Permanent still
        /// refreshes normally, Permanent-over-Permanent included, reassigning
        /// <see cref="EffectInstance.Source"/> to the latest applier.</para>
        /// </summary>
        public static void Apply(List<EffectInstance> effects, EffectInstance instance)
        {
            for (int i = 0; i < effects.Count; i++)
            {
                if (effects[i].Kind != instance.Kind) continue;
                bool existingIsPermanent = effects[i].Scope == EffectScope.Permanent;
                bool refreshes = instance.Scope == EffectScope.Permanent ||
                    (!existingIsPermanent && instance.TurnsRemaining > effects[i].TurnsRemaining);
                if (refreshes)
                {
                    effects[i].Scope = instance.Scope;
                    effects[i].TurnsRemaining = instance.TurnsRemaining;
                    effects[i].Source = instance.Source;
                }
                return;
            }
            effects.Add(instance);
        }

        public static void Remove(List<EffectInstance> effects, EffectKind kind)
            => effects.RemoveAll(e => e.Kind == kind);

        /// <summary>
        /// Turns still owed by the instance of <paramref name="kind"/>; 0 if absent. Direct lookup, not
        /// a fold — the one-per-kind invariant on <see cref="EffectList"/> means at most one match ever
        /// exists, so there is nothing to take a max over.
        /// </summary>
        public static int TurnsRemaining(List<EffectInstance> effects, EffectKind kind)
        {
            for (int i = 0; i < effects.Count; i++)
                if (effects[i].Kind == kind) return effects[i].TurnsRemaining;
            return 0;
        }

        /// <summary>START of the host controller's turn: age every turn-scoped instance.</summary>
        public static void TickControllerTurnStart(List<EffectInstance> effects)
        {
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (e.Scope == EffectScope.ControllerTurns && e.TurnsRemaining > 0) e.TurnsRemaining--;
            }
        }

        /// <summary>
        /// END of the host controller's turn: expire what has served its time. An instance applied
        /// during this very turn still reads <c>TurnsRemaining &gt; 0</c> here — it was never aged —
        /// so it survives to cost a full turn.
        /// </summary>
        public static void ExpireControllerTurnEnd(List<EffectInstance> effects)
            => effects.RemoveAll(e => e.Scope == EffectScope.ControllerTurns && e.TurnsRemaining <= 0);
    }
}
