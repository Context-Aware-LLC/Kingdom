using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
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
    /// <para>An unmapped class returns <see cref="string.Empty"/> rather than throwing, preserving the
    /// behaviour the match-start path has always had: NEUTRAL is not a hero and has no power.</para>
    /// </summary>
    public static class HeroPowers
    {
        public static string ForClass(CardClass cls) => cls switch
        {
            CardClass.EMBERWARD => "hero_power_searing_bolt",
            CardClass.TIDECALLER => "hero_power_tidal_ward",
            CardClass.GRAVEKEEP => "hero_power_toll_the_bell",
            CardClass.VERDANT => "hero_power_quicken",
            CardClass.CLOCKWRIGHT => "hero_power_blueprint",
            CardClass.NIGHTVEIL => "hero_power_shadow_mark",
            CardClass.STONEWARDEN => "hero_power_earthwall",
            CardClass.TEMPEST => "hero_power_chain_spark",
            _ => "",
        };
    }
}
