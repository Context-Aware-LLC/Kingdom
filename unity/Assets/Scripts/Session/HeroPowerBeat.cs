using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>How a hero power's effect travels from its socket to what it hits.</summary>
    public enum HeroPowerDelivery
    {
        /// <summary>One bolt off the socket that forks part-way and strikes every target at the
        /// same moment (Tempest's Chain Spark).</summary>
        ForkedLightning,
    }

    /// <summary>
    /// The delivery beat a hero power plays BEFORE its result is applied: who fired, what it hit,
    /// and how long the entry is held so the strike lands as the board redraws. Carried on a
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

        public HeroPowerBeat(HeroPowerDelivery delivery, InstanceId hero,
                             IReadOnlyList<InstanceId> targets, float holdSeconds)
        {
            Delivery = delivery;
            Hero = hero;
            Targets = targets;
            HoldSeconds = holdSeconds;
        }
    }

    /// <summary>
    /// Decides whether a command's batch is a hero power with a delivery to show.
    ///
    /// <para><b>Keyed on the hero's class, not on the power's card.</b> A delivery is a look, and
    /// the look belongs to the class. Only classes listed in <see cref="DeliveryFor"/> get a beat;
    /// every other hero power resolves exactly as it did before this existed.</para>
    /// </summary>
    public static class HeroPowerBeatClassifier
    {
        public static HeroPowerBeat? Classify(GameState snapshot, IReadOnlyList<GameEvent> events,
                                              float holdSeconds)
        {
            if (snapshot == null || events == null || holdSeconds <= 0f) return null;

            for (int i = 0; i < events.Count; i++)
            {
                GameEvent used = events[i];
                if (used.Kind != GameEventKind.HeroPowerUsed || !used.Source.HasValue) continue;

                InstanceId hero = used.Source.Value;
                if (!TryClassOf(snapshot, hero, out CardClass heroClass)) return null;
                HeroPowerDelivery? delivery = DeliveryFor(heroClass);
                if (!delivery.HasValue) return null;

                // The power's own damage carries the hero as its source (ApplyHeroPower raises the
                // effect with the hero's id). Anything a hit goes on to cause — a deathrattle, a
                // retaliation — has another source and is not part of the delivery.
                var targets = new List<InstanceId>();
                for (int j = i + 1; j < events.Count; j++)
                {
                    GameEvent e = events[j];
                    if (e.Kind != GameEventKind.DamageApplied || !e.Target.HasValue) continue;
                    if (!e.Source.HasValue || e.Source.Value != hero) continue;
                    if (!targets.Contains(e.Target.Value)) targets.Add(e.Target.Value);
                }

                if (targets.Count == 0) return null;
                return new HeroPowerBeat(delivery.Value, hero, targets, holdSeconds);
            }

            return null;
        }

        private static HeroPowerDelivery? DeliveryFor(CardClass heroClass) => heroClass switch
        {
            CardClass.TEMPEST => HeroPowerDelivery.ForkedLightning,
            _                 => (HeroPowerDelivery?)null,
        };

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
