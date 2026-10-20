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
    /// <para><b>The words themselves live in <c>Resources/Localization/values/glossary.xml</c></b>
    /// (2026-10-20), as <c>keyword_&lt;id&gt;</c> and <c>keyword_&lt;id&gt;_desc</c>, read through
    /// <see cref="GameTerms"/> so every language has one name and one line per keyword. The comments
    /// below stay here because they record what the ENGINE does, which is what a translator of the
    /// line must not lose.</para>
    ///
    /// <para><b>The English names must stay equal to <c>CardTextGenerator.Name(keyword)</c></b>, because
    /// the card body is still generated English and <c>CardTextStyling</c> bolds it by matching those
    /// names. <see cref="KeywordSemantics.AuthoredName"/> still outranks the glossary for the same reason.</para>
    /// </summary>
    internal static class KeywordCopy
    {
        /// <summary>The keyword's player-facing name in the player's language.</summary>
        internal static string DisplayName(Keyword kw) =>
            KeywordSemantics.AuthoredName(kw) ?? GameTerms.Keyword(kw);

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
        // What each line is written against — the ENGINE site, not the genre convention of the same name:
        // CommandProcessor.ValidateAttack: a non-shrouded Bulwark on the defending board
        // restricts every attack to Bulwarks. Front-row position carries this visually.
        //   -> Keyword.BULWARK
        //
        // CommandProcessor.ValidateAttack: FERVOR waives summoning sickness outright.
        //   -> Keyword.FERVOR
        //
        // Same site, plus the explicit minion-only restriction during the summoning turn.
        //   -> Keyword.SKIRMISH
        //
        // DamagePipeline: Aegis absorbs the WHOLE hit and then clears — no health lost and, by
        // G0 ruling R-2(c), no on-hit riders land either.
        //   -> Keyword.AEGIS
        //
        // DamagePipeline: the SOURCE'S CONTROLLER's hero heals for the damage dealt — the hero,
        // never the minion itself.
        //   -> Keyword.LIFEDRAIN
        //
        // CommandProcessor.ValidateAttack: maxAttacks becomes 2.
        //   -> Keyword.DOUBLESTRIKE
        //
        // ValidateAttack + ValidatePlayCard: Shroud hides a minion from attacks and from
        // targeting, and CommandProcessor strips it the moment the minion declares an attack.
        //   -> Keyword.SHROUD
        //
        // DamagePipeline: any damage to a MINION sets health to 0. Heroes are unaffected.
        //   -> Keyword.VENOM
        //
        // ActionHandlers: one bonus point of damage from spells per copy on your board.
        //   -> Keyword.ARCANE_SURGE
        //
        //   -> Keyword.SILENCE (an action marker; no engine note)
        //
        // DamagePipeline's Frost rider affects WHATEVER IT DAMAGES, heroes included — unlike
        // Venom above, which is minions only.
        //   -> Keyword.FROST
        //
        // BackdraftHandler: adds to NextTurnBackdraftLock, spent at the start of your next turn.
        //   -> Keyword.BACKDRAFT
        //
        // ActionHandlers.DrownHandler: half the target's CURRENT health rounded up to a
        // minion, a flat 2 to a hero. DamagePipeline runs it with ignoresAegis: true (Sam,
        // 2026-09-05: "divine shield can't protect you against drowning").
        //   -> Keyword.DROWN
        //
        // EffectInterpreter.Resummon: the copy comes back at exactly 1 health, in the same
        // board slot, and can never rekindle again.
        //   -> Keyword.REKINDLE
        //
        // CommandProcessor: excess damage to a MINION carries to the enemy hero. The overflow
        // hit travels with a null keyword set, so Lifedrain/Venom/Frost do NOT ride along.
        //   -> Keyword.OVERWHELM
        //
        // CommandProcessor phase 2: retaliation is skipped when the defender died to the hit.
        //   -> Keyword.VANGUARD
        //
        // Reserved ordinal slot, NOT carrier behavior (KeywordSemantics.IsActionMarker), so C25
        // rejects it in keywords[] and BuildBody — which only looks up a card's authored keywords
        // — can never reach this line. It exists solely to satisfy CardRevealVerify's enum walk.
        // No player meets this word: stat changes print their numbers directly (CardTextGenerator),
        // so its glossary row is a placeholder marked translatable="false", not copy.
        //   -> Keyword.REINFORCE
        //
        // A keyword added to the enum without a glossary row returns null, and CardRevealVerify names it.
        internal static string Glossary(Keyword kw) => GameTerms.KeywordGlossary(kw);
    }
}
