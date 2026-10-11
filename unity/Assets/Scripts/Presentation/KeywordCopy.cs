using System.Text;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The ONE player-facing copy surface for keywords: the humanised <see cref="DisplayName"/> and
    /// the one-line rules <see cref="Glossary"/>. The card face (CardPresenter.ApplyBodyText) and the
    /// hover-reveal panel (CardRevealContent) both read from here, so a card and its own tooltip
    /// cannot drift apart.
    ///
    /// <para>DisplayName moved here from CardPresenter rather than being copied: a second
    /// humaniser is exactly the duplicate-surface failure the keyword-display backlog exists to
    /// close, and the tooltip is the second consumer that would have created it.</para>
    ///
    /// <para><b>This file deliberately does NOT decide keyword NAMES.</b> Enum ids are canonical;
    /// <see cref="DisplayName"/> only formats them for player-facing copy, deferring to
    /// <see cref="KeywordSemantics.AuthoredName"/> for the ones whose printed word is not their id.
    /// The GLOSSARY BODY owns the short description of each carrier mechanic.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER.</b> When the keyword display layer lands and every keyword gains
    /// an authored display name, delete <see cref="DisplayName"/>'s humanising fallback and let
    /// <see cref="KeywordSemantics.AuthoredName"/> answer outright — the authored-name lookup this
    /// trigger anticipated now exists, and it lives in Core.Cards rather than here so the card face
    /// can reach it too. Do not re-introduce a per-consumer copy.</para>
    /// </summary>
    internal static class KeywordCopy
    {
        /// <summary>Interim display map (keyword-display backlog): enum ids stay uppercase by
        /// design; humanise for player-facing surfaces until the full display layer lands.</summary>
        internal static string DisplayName(Keyword kw)
        {
            // An authored name outranks the humaniser, and the card face resolves the same call, so
            // a card and its own tooltip cannot disagree. No keyword overrides its id today — FROST
            // was the last one, and since 2026-08-25 it humanises to "Frost" like everything else,
            // the verb "Freeze" having moved to CardTextGenerator.FrostVerb.
            string authored = KeywordSemantics.AuthoredName(kw);
            if (authored != null) return authored;

            string[] parts = kw.ToString().Split('_');
            var sb = new StringBuilder();
            foreach (string p in parts)
            {
                if (p.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }

        /// <summary>
        /// One line of rules copy per keyword, written against what the ENGINE actually does — not
        /// against the genre convention of the same name. Each line names its source below, because
        /// several of these differ from what a Hearthstone player would assume.
        ///
        /// <para>Every <see cref="Keyword"/> value must have a line. CardRevealVerify walks
        /// <c>Enum.GetValues</c> and fails the headless harness on any that returns null, so a
        /// keyword added without copy cannot ship silently. Null rather than a placeholder string
        /// on purpose: a placeholder reads as finished copy and would pass any check that only
        /// tests for emptiness.</para>
        /// </summary>
        internal static string Glossary(Keyword kw) => kw switch
        {
            // CommandProcessor.ValidateAttack: a non-shrouded Bulwark on the defending board
            // restricts every attack to Bulwarks. Front-row position carries this visually.
            Keyword.BULWARK =>
                "Enemy attacks have to go through this minion first.",

            // CommandProcessor.ValidateAttack: FERVOR waives summoning sickness outright.
            Keyword.FERVOR =>
                "Can attack the turn it is played, hero included.",

            // Same site, plus the explicit minion-only restriction during the summoning turn.
            Keyword.SKIRMISH =>
                "Can attack the turn it is played, but not the enemy hero.",

            // DamagePipeline: Aegis absorbs the WHOLE hit and then clears — no health lost and, by
            // G0 ruling R-2(c), no on-hit riders land either.
            Keyword.AEGIS =>
                "Absorbs the next hit completely, then breaks.",

            // DamagePipeline: the SOURCE'S CONTROLLER's hero heals for the damage dealt — the hero,
            // never the minion itself.
            Keyword.LIFEDRAIN =>
                "Damage this deals also heals your hero by the same amount.",

            // CommandProcessor.ValidateAttack: maxAttacks becomes 2.
            Keyword.DOUBLESTRIKE =>
                "Can attack twice each turn.",

            // ValidateAttack + ValidatePlayCard: Shroud hides a minion from attacks and from
            // targeting, and CommandProcessor strips it the moment the minion declares an attack.
            Keyword.SHROUD =>
                "Can't be attacked or targeted until it attacks.",

            // DamagePipeline: any damage to a MINION sets health to 0. Heroes are unaffected.
            Keyword.VENOM =>
                "Any minion this damages is destroyed.",

            // ActionHandlers: one bonus point of damage from spells per copy on your board.
            Keyword.ARCANE_SURGE =>
                "Your spells deal 1 more damage. Copies add up.",

            Keyword.SILENCE =>
                "Strips a minion's text, keywords and buffs.",

            // DamagePipeline's Frost rider affects WHATEVER IT DAMAGES, heroes included — unlike
            // Venom above, which is minions only.
            Keyword.FROST =>
                "Whatever this damages misses its next attack — heroes too.",

            // BackdraftHandler: adds to NextTurnBackdraftLock, spent at the start of your next turn.
            Keyword.BACKDRAFT =>
                "Locks that much of your mana on your next turn.",

            // ActionHandlers.DrownHandler: half the target's CURRENT health rounded up to a
            // minion, a flat 2 to a hero. DamagePipeline runs it with ignoresAegis: true (Sam,
            // 2026-09-05: "divine shield can't protect you against drowning").
            Keyword.DROWN =>
                "Deals half this minion's Health, rounded up — 2 to a hero. Ignores Aegis.",

            // EffectInterpreter.Resummon: the copy comes back at exactly 1 health, in the same
            // board slot, and can never rekindle again.
            Keyword.REKINDLE =>
                "The first time it dies it returns in place with 1 Health.",

            // CommandProcessor: excess damage to a MINION carries to the enemy hero. The overflow
            // hit travels with a null keyword set, so Lifedrain/Venom/Frost do NOT ride along.
            Keyword.OVERWHELM =>
                "Excess damage from its attack spills onto the enemy hero.",

            // CommandProcessor phase 2: retaliation is skipped when the defender died to the hit.
            Keyword.VANGUARD =>
                "Takes no damage back when its attack kills the defender.",

            // Reserved ordinal slot, NOT carrier behavior (KeywordSemantics.IsActionMarker), so C25
            // rejects it in keywords[] and BuildBody — which only looks up a card's authored keywords
            // — can never reach this line. It exists solely to satisfy CardRevealVerify's enum walk.
            // No player meets this word: stat changes print their numbers directly (CardTextGenerator),
            // so no glossary entry is owed and the string below is a placeholder, not copy.
            Keyword.REINFORCE =>
                "Gives a friendly minion +1/+1.",

            // A keyword added to the enum without copy lands here. CardRevealVerify names it.
            _ => null,
        };
    }
}
