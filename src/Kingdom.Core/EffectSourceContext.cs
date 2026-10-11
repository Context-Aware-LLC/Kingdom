using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core
{
    /// <summary>
    /// Immutable source facts captured when a rule is queued. <see cref="CurrentCarrier"/> is an
    /// optional live-state convenience; rule meaning never depends on it remaining in play.
    /// </summary>
    public sealed class EffectSourceContext
    {
        public InstanceId Identity { get; }
        public PlayerId Controller { get; }
        public CardDefinition Definition { get; }
        public RuleDefinition Rule { get; }
        public RuleRef RuleReference { get; }
        public EffectSourceKind Kind { get; }
        public IReadOnlyList<Keyword> SourceKeywords { get; }
        public CardInstance? CurrentCarrier { get; }

        public EffectSourceContext(InstanceId identity, PlayerId controller,
            CardDefinition definition, RuleDefinition rule, EffectSourceKind kind,
            CardInstance? currentCarrier)
        {
            Identity = identity;
            Controller = controller;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            RuleReference = new RuleRef(definition.Id, rule.Id);
            Kind = kind;
            bool carrierOwnsEffectiveKeywords = currentCarrier != null &&
                (kind == EffectSourceKind.BoardMinion ||
                 kind == EffectSourceKind.EquippedWeapon);
            var keywords = new List<Keyword>(carrierOwnsEffectiveKeywords
                ? currentCarrier!.Keywords
                : definition.Keywords);
            keywords.Sort((left, right) => ((int)left).CompareTo((int)right));
            SourceKeywords = keywords.AsReadOnly();
            CurrentCarrier = currentCarrier;
        }
    }

    /// <summary>Immutable event facts kept separate from the effect source that observes them.</summary>
    public readonly struct TriggerEventContext
    {
        public PlayerId? IncomingTurnPlayer { get; }
        public InstanceId? TriggerSource { get; }

        private TriggerEventContext(PlayerId? incomingTurnPlayer, InstanceId? triggerSource)
        {
            IncomingTurnPlayer = incomingTurnPlayer;
            TriggerSource = triggerSource;
        }

        public static TriggerEventContext ForIncomingTurn(PlayerId incomingTurnPlayer)
            => new TriggerEventContext(incomingTurnPlayer, null);

        public static TriggerEventContext ForSource(InstanceId source)
            => new TriggerEventContext(null, source);

        public TriggerEventContext WithTriggerSource(InstanceId source)
            => new TriggerEventContext(IncomingTurnPlayer, source);
    }
}
