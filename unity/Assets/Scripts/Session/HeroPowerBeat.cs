using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>How a hero power's effect travels from its socket to what it hits.</summary>
    public enum HeroPowerDelivery
    {
        /// <summary>Nothing travels: the cast shows on the hero's portrait and nowhere else.</summary>
        None,
        /// <summary>One bolt off the socket that forks part-way and strikes every target at the
        /// same moment (Tempest's Chain Spark).</summary>
        ForkedLightning,
        /// <summary>A thin green bolt from Gravekeep's power to one weakened minion.</summary>
        GravekeepSappingBolt,
        /// <summary>A single fire bolt from Pyra's socket to the Searing Bolt target.</summary>
        EmberwardSearingBolt,
        /// <summary>Nightveil's shadow rises beside the enemy hero for Backstab.</summary>
        NightveilBackstabShadow,
        StonewardenSummon,
        TidecallerSummon,
        /// <summary>Blueprint light gathers at the caster's deck before its draw resolves.</summary>
        ClockwrightBlueprint,
    }

    /// <summary>How long a hero power entry is held, per look. Owned by the presentation, which
    /// knows how long its own animations run; passed in so this assembly holds no timings.</summary>
    public readonly struct HeroPowerHolds
    {
        /// <summary>A cast that only shows on the portrait.</summary>
        public readonly float CastOnly;
        /// <summary>A cast followed by forked lightning, start of the cast to the strike.</summary>
        public readonly float ForkedLightning;
        public readonly float GravekeepSappingBolt;
        public readonly float EmberwardSearingBolt;
        public readonly float NightveilBackstabShadow;
        public readonly float SummonEffect;
        public readonly float ClockwrightBlueprint;

        public HeroPowerHolds(float castOnly, float forkedLightning, float gravekeepSappingBolt = 0f,
                              float emberwardSearingBolt = 0f, float nightveilBackstabShadow = 0f,
                              float summonEffect = 1.35f, float clockwrightBlueprint = 0f)
        {
            CastOnly = castOnly;
            ForkedLightning = forkedLightning;
            GravekeepSappingBolt = gravekeepSappingBolt;
            EmberwardSearingBolt = emberwardSearingBolt;
            NightveilBackstabShadow = nightveilBackstabShadow;
            SummonEffect = summonEffect;
            ClockwrightBlueprint = clockwrightBlueprint;
        }
    }

    /// <summary>
    /// The beat a hero power plays BEFORE its result is applied: who fired, what it hit, and how
    /// long the entry is held so the cast finishes as the board redraws. Carried on a
    /// <see cref="PlaybackQueue.Entry"/> exactly as a <see cref="PlayReveal"/> is, and for the
    /// same reason — the beat belongs to the moment the entry reaches the front, not to the moment
    /// its command resolved.
    /// </summary>
    public readonly struct HeroPowerBeat
    {
        public readonly HeroPowerDelivery Delivery;
        /// <summary>The hero whose power fired. Its socket is where the delivery starts.</summary>
        public readonly InstanceId Hero;
        /// <summary>What the power hit, in emission order, each once.</summary>
        public readonly IReadOnlyList<InstanceId> Targets;
        public readonly float HoldSeconds;
        public readonly PlayerId SummonController;
        public readonly int SummonSlot;
        public readonly int SummonBoardCount;

        public HeroPowerBeat(HeroPowerDelivery delivery, InstanceId hero,
                             IReadOnlyList<InstanceId> targets, float holdSeconds,
                             PlayerId summonController = default, int summonSlot = -1,
                             int summonBoardCount = 0)
        {
            Delivery = delivery;
            Hero = hero;
            Targets = targets;
            HoldSeconds = holdSeconds;
            SummonController = summonController;
            SummonSlot = summonSlot;
            SummonBoardCount = summonBoardCount;
        }
    }

    /// <summary>
    /// Decides whether a command's batch is a hero power with a cast to show.
    ///
    /// <para><b>Keyed on the hero's class, not on the power's card.</b> A cast is a look, and the
    /// look belongs to the class. Only classes listed in <see cref="TryLookFor"/> get a beat;
    /// every other hero power resolves exactly as it did before this existed.</para>
    /// </summary>
    public static class HeroPowerBeatClassifier
    {
        public static HeroPowerBeat? Classify(GameState snapshot, IReadOnlyList<GameEvent> events,
                                              HeroPowerHolds holds)
        {
            if (snapshot == null || events == null) return null;

            for (int i = 0; i < events.Count; i++)
            {
                GameEvent used = events[i];
                if (used.Kind != GameEventKind.HeroPowerUsed || !used.Source.HasValue) continue;

                InstanceId hero = used.Source.Value;
                if (!TryClassOf(snapshot, hero, out CardClass heroClass)) return null;
                if (!TryLookFor(heroClass, out HeroPowerDelivery delivery)) return null;

                PlayerId summonController = default;
                int summonSlot = -1, summonBoardCount = 0;
                if (delivery == HeroPowerDelivery.StonewardenSummon || delivery == HeroPowerDelivery.TidecallerSummon)
                {
                    int tokenDefinition = delivery == HeroPowerDelivery.StonewardenSummon ? 1060 : 1024;
                    bool found = false;
                    for (int j = i + 1; j < events.Count && !found; j++)
                    {
                        GameEvent e = events[j];
                        if (e.Kind != GameEventKind.CardSummoned || !e.Source.HasValue) continue;
                        foreach (PlayerState ps in snapshot.Players)
                            for (int slot = 0; slot < ps.Board.Count; slot++)
                                if (ps.Board[slot].Id == e.Source.Value && ps.Board[slot].DefinitionId == tokenDefinition)
                                {
                                    summonController = ps.Id;
                                    summonSlot = slot;
                                    summonBoardCount = ps.Board.Count;
                                    found = true;
                                    break;
                                }
                    }
                    if (!found) return null;
                }

                // The power's own result carries the hero as its source. Gravekeep weakens through
                // StatsModified rather than damage; follow-up effects have their own source and
                // are not part of this delivery.
                var targets = new List<InstanceId>();
                for (int j = i + 1; j < events.Count; j++)
                {
                    GameEvent e = events[j];
                    bool delivered = e.Kind == GameEventKind.DamageApplied
                                  || (heroClass == CardClass.GRAVEKEEP && e.Kind == GameEventKind.StatsModified);
                    if (!delivered || !e.Target.HasValue) continue;
                    if (!e.Source.HasValue || e.Source.Value != hero) continue;
                    if (!targets.Contains(e.Target.Value)) targets.Add(e.Target.Value);
                }

                // Lightning with nothing to strike is just the cast.
                if (targets.Count == 0 && delivery != HeroPowerDelivery.StonewardenSummon
                                        && delivery != HeroPowerDelivery.TidecallerSummon
                                        && delivery != HeroPowerDelivery.ClockwrightBlueprint)
                    delivery = HeroPowerDelivery.None;
                float hold = delivery switch
                {
                    HeroPowerDelivery.ForkedLightning => holds.ForkedLightning,
                    HeroPowerDelivery.GravekeepSappingBolt => holds.GravekeepSappingBolt,
                    HeroPowerDelivery.EmberwardSearingBolt => holds.EmberwardSearingBolt,
                    HeroPowerDelivery.NightveilBackstabShadow => holds.NightveilBackstabShadow,
                    HeroPowerDelivery.StonewardenSummon => holds.SummonEffect,
                    HeroPowerDelivery.TidecallerSummon => holds.SummonEffect,
                    HeroPowerDelivery.ClockwrightBlueprint => holds.ClockwrightBlueprint,
                    _ => holds.CastOnly,
                };
                if (hold <= 0f) return null;
                return new HeroPowerBeat(delivery, hero, targets, hold, summonController, summonSlot, summonBoardCount);
            }

            return null;
        }

        /// <summary>The classes whose hero power has a cast to show, and what travels after it.
        /// A class added here needs its portrait art in <c>PortraitCastEffect</c> too.</summary>
        private static bool TryLookFor(CardClass heroClass, out HeroPowerDelivery delivery)
        {
            switch (heroClass)
            {
                case CardClass.VERDANT: delivery = HeroPowerDelivery.None; return true;
                case CardClass.CLOCKWRIGHT: delivery = HeroPowerDelivery.ClockwrightBlueprint; return true;
                case CardClass.EMBERWARD: delivery = HeroPowerDelivery.EmberwardSearingBolt; return true;
                case CardClass.TEMPEST: delivery = HeroPowerDelivery.ForkedLightning; return true;
                case CardClass.GRAVEKEEP: delivery = HeroPowerDelivery.GravekeepSappingBolt; return true;
                case CardClass.NIGHTVEIL: delivery = HeroPowerDelivery.NightveilBackstabShadow; return true;
                case CardClass.STONEWARDEN: delivery = HeroPowerDelivery.StonewardenSummon; return true;
                case CardClass.TIDECALLER: delivery = HeroPowerDelivery.TidecallerSummon; return true;
                default: delivery = HeroPowerDelivery.None; return false;
            }
        }

        private static bool TryClassOf(GameState snapshot, InstanceId hero, out CardClass heroClass)
        {
            foreach (PlayerState ps in snapshot.Players)
            {
                if (ps.Hero == null || ps.Hero.Id != hero) continue;
                heroClass = ps.Hero.Class;
                return true;
            }
            heroClass = default;
            return false;
        }
    }
}
