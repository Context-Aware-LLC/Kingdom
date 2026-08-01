using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Editor
{
    /// <summary>
    /// Mutable working-copy model the authoring window edits against. CardDefinition/EffectDef are
    /// immutable POCOs (by design — runtime state never lives on them), so the window needs its own
    /// editable mirror; ToDefinition()/FromDefinition() are the only conversion points, keeping the
    /// canonical JSON (via CardJsonRowIO.Serialize) as the single source of truth once saved.
    /// </summary>
    public sealed class EditableCard
    {
        public string Id = "";
        public string Name = "";
        public CardType Type = CardType.MINION;
        public int Cost;
        public bool HasAttack; public int Attack;
        public bool HasHealth; public int Health;
        public bool HasDurability; public int Durability;
        public CardClass Class = CardClass.NEUTRAL;
        public Rarity Rarity = Rarity.COMMON;
        public List<Keyword> Keywords = new List<Keyword>();
        public List<EditableEffect> Effects = new List<EditableEffect>();
        public string CardText = "";
        public string FlavorText = "";
        public string ArtId = "";
        public SetId SetId = SetId.CORE;
        public bool Collectible = true;
        public bool HasTribe; public Tribe Tribe;
        public string FactionId = "";
        public bool RequiresEngineeringPrimitive;

        public static EditableCard FromDefinition(CardDefinition def) => new EditableCard
        {
            Id = def.Id,
            Name = def.Name,
            Type = def.Type,
            Cost = def.Cost,
            HasAttack = def.Attack.HasValue, Attack = def.Attack ?? 0,
            HasHealth = def.Health.HasValue, Health = def.Health ?? 1,
            HasDurability = def.Durability.HasValue, Durability = def.Durability ?? 1,
            Class = def.Class,
            Rarity = def.Rarity,
            Keywords = new List<Keyword>(def.Keywords),
            Effects = def.Effects.Select(EditableEffect.FromEffectDef).ToList(),
            CardText = def.CardText,
            FlavorText = def.FlavorText,
            ArtId = def.ArtId,
            SetId = def.SetId,
            Collectible = def.Collectible,
            HasTribe = def.Tribe.HasValue, Tribe = def.Tribe ?? default,
            FactionId = def.FactionId,
            RequiresEngineeringPrimitive = def.RequiresEngineeringPrimitive,
        };

        public CardDefinition ToDefinition() => new CardDefinition(
            id: Id, name: Name, type: Type, cost: Cost,
            attack: HasAttack ? Attack : (int?)null,
            health: HasHealth ? Health : (int?)null,
            durability: HasDurability ? Durability : (int?)null,
            @class: Class, rarity: Rarity,
            keywords: Keywords, effects: Effects.Select(e => e.ToEffectDef()).ToList(),
            cardText: CardText, flavorText: FlavorText, artId: ArtId, setId: SetId,
            collectible: Collectible, tribe: HasTribe ? Tribe : (Tribe?)null,
            factionId: FactionId, requiresEngineeringPrimitive: RequiresEngineeringPrimitive);

        public EditableCard Clone()
        {
            var c = FromDefinition(ToDefinition());
            return c;
        }
    }

    public sealed class EditableEffect
    {
        public Trigger Trigger = Trigger.ON_PLAY;
        public Action Action = Action.DEAL_DAMAGE;
        public bool HasAmount; public int Amount;
        public bool HasAmount2; public int Amount2;
        public bool HasTokenId; public string TokenId = "";
        public bool HasKeyword; public Keyword Keyword;
        public bool HasVfxOverride; public VfxEvent VfxOverride;
        public bool HasSelector; public string Selector = "";
        public bool Permanent; public bool Empty; // GAIN_MANA payload flags (dev-docs/specs/card-schema.md §5.2)

        public static EditableEffect FromEffectDef(EffectDef fx) => new EditableEffect
        {
            Trigger = fx.Trigger,
            Action = fx.Action,
            HasAmount = fx.Amount.HasValue, Amount = fx.Amount ?? 0,
            HasAmount2 = fx.Amount2.HasValue, Amount2 = fx.Amount2 ?? 0,
            HasTokenId = !string.IsNullOrEmpty(fx.TokenId), TokenId = fx.TokenId ?? "",
            HasKeyword = fx.GrantedKeyword.HasValue, Keyword = fx.GrantedKeyword ?? default,
            HasVfxOverride = fx.VfxOverride.HasValue, VfxOverride = fx.VfxOverride ?? default,
            HasSelector = !string.IsNullOrEmpty(fx.Selector), Selector = fx.Selector ?? "",
            Permanent = fx.Permanent, Empty = fx.Empty,
        };

        public EffectDef ToEffectDef() => new EffectDef(
            Trigger, Action,
            HasAmount ? Amount : (int?)null,
            HasAmount2 ? Amount2 : (int?)null,
            HasTokenId ? TokenId : null,
            HasKeyword ? Keyword : (Keyword?)null,
            HasVfxOverride ? VfxOverride : (VfxEvent?)null,
            HasSelector ? Selector : null,
            Permanent, Empty);
    }
}
