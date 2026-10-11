using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>
    /// The single class → hero-power-card mapping. <b>Every surface that needs to know which hero
    /// power a class carries must come through here</b> — the match-start sequence that stamps
    /// <c>Hero.HeroPowerId</c>, and the character-creation detail overlay that shows a class's power
    /// before the player commits to it.
    ///
    /// <para>It is a seam rather than a duplicated <c>switch</c> for the reason already written into
    /// <c>CharacterRosterController.PairableClasses</c>: a second hardcoded copy of a per-class table
    /// is what stranded four classes in the create flow that already had cards. A class whose power
    /// changes, or a ninth class, updates one method instead of two that nothing compares.</para>
    ///
    /// <para>An unmapped class returns 0 rather than throwing, preserving the behaviour the
    /// match-start path has always had: NEUTRAL is not a hero and has no power. 0 is safe as that
    /// signal precisely because no card is ever numbered 0.</para>
    ///
    /// <para><b>The numbers are written out rather than looked up, and that is correct.</b> A card
    /// id is permanent, so a literal here cannot go stale the way a handle could — and resolving
    /// these through the handle instead would make the handle load-bearing, which is the one thing
    /// the identity model forbids. The comment beside each is a label, not a lookup key.</para>
    /// </summary>
    public static class HeroPowers
    {
        public static int ForClass(CardClass cls) => cls switch
        {
            CardClass.EMBERWARD => 1,    // hero_power_searing_bolt
            CardClass.TIDECALLER => 2,   // hero_power_tidal_ward
            CardClass.GRAVEKEEP => 3,    // hero_power_weaken
            CardClass.VERDANT => 4,      // hero_power_quicken
            CardClass.CLOCKWRIGHT => 5,  // hero_power_blueprint
            CardClass.NIGHTVEIL => 6,    // hero_power_backstab
            CardClass.STONEWARDEN => 7,  // hero_power_earthwall
            CardClass.TEMPEST => 8,      // hero_power_chain_spark
            _ => 0,
        };
    }
}
