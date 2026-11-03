using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Spell-school effects for played spells. Every spell of a school's class plays its caster
    /// effect; a damaged target gets the school's missile and impact, any other target the spell
    /// acts on gets the impact alone (<see cref="Burst"/>). Earth (a buildup on the caster, a
    /// rock falling on each target, an impact where it lands), lightning (a charge under the
    /// caster, an arc or a bolt to each target, a hit on it), clockwright (gears gathering on the
    /// caster, parts flying to each target, a burst of them where they land), water and frost
    /// (Tidecaller: a wave and a splash, a drowned target washed; an ice shard and the freeze),
    /// gravekeep (grave mist on the caster, a skull to each target, soulfire where it lands),
    /// verdant (vines on the caster, a vine to each target, a wreath closing on it), fire (a flare on the caster, a fireball to each target, a burst
    /// where it lands), shadow (a black wipe across the caster's portrait, a wisp to each target,
    /// a ring where it lands), the healing sparkles and shimmer on everything a spell heals,
    /// and the green poof on a minion a Venom source damages. Each sheet is three frames side by
    /// side. Kept apart from the hero power effects on purpose:
    /// similar, not the same (Sam, 2026-10-13).
    ///
    /// <para>Timing is all relative to the play reveal's start, the moment
    /// <see cref="VfxDirector.ShowPlayReveal"/> is called. The entry releases, and the damage or
    /// healing shows, at <c>PlayReveal.HoldSeconds</c>; the earth rocks are scheduled to land
    /// before that, the last one exactly on it.</para>
    /// </summary>
    internal enum LightningShape { Across, FromAbove, Chain }

    internal static class SpellSchoolEffects
    {
        /// <summary>
        /// How much slower and bigger every spell effect, and every hero power effect, plays than
        /// it was first drawn (Sam, 2026-11-03: "too fast and possibly also too small"). Every
        /// duration below and in the hero power effects is a base value times <see cref="Tempo"/>,
        /// and every drawn size passes through <see cref="Size"/>, so these two numbers retune the
        /// whole set together. The playback queue's holds are computed from the same durations,
        /// so the board still redraws as each effect lands.
        /// </summary>
        public const float Tempo = 1.5f;
        public const float Size = 1.3f;

        public const string FireFlameKey = "effect_spell_fire_flame";
        public const string FireBuildupKey = "effect_spell_fire_buildup";
        public const string FireImpactKey = "effect_spell_fire_impact";
        public const string ShadowTravelKey = "effect_spell_shadow_travel";
        public const string ShadowImpactKey = "effect_spell_shadow_impact";
        public const string EarthBuildupKey = "effect_spell_earth_buildup";
        public const string EarthRockKey = "effect_spell_earth_rock";
        public const string EarthImpactKey = "effect_spell_earth_impact";
        public const string HealShimmerKey = "effect_spell_heal_shimmer";
        public const string VenomPoofKey = "effect_venom_hit_poof";
        public const string HealTravelKey = "effect_spell_heal_travel";
        public const string LightningArcKey = "effect_spell_lightning_arc";
        public const string LightningStrikeKey = "effect_spell_lightning_strike";
        public const string LightningChargeKey = "effect_spell_lightning_charge";
        public const string LightningHitKey = "effect_spell_lightning_hit";
        public const string ClockwrightCasterKey = "effect_spell_clockwright_caster";
        public const string ClockwrightTravelKey = "effect_spell_clockwright_travel";
        public const string ClockwrightTargetKey = "effect_spell_clockwright_target";
        public const string WaterCasterKey = "effect_spell_water_caster";
        public const string WaterTravelKey = "effect_spell_water_travel";
        public const string WaterSplashKey = "effect_spell_water_splash";
        public const string FrostCasterKey = "effect_spell_frost_caster";
        public const string FrostTravelKey = "effect_spell_frost_travel";
        public const string DrownWashKey = "effect_drown_wash";
        public const string GravekeepCasterKey = "effect_spell_gravekeep_caster";
        public const string GravekeepTravelKey = "effect_spell_gravekeep_travel";
        public const string GravekeepTargetKey = "effect_spell_gravekeep_target";
        public const string VerdantCasterKey = "effect_spell_verdant_caster";
        public const string VerdantTravelKey = "effect_spell_verdant_travel";
        public const string VerdantTargetKey = "effect_spell_verdant_target";

        /// <summary>The first rock lands this long after the reveal card is gone, and each further
        /// target this long after the one before. VfxDirector hands both to the classifier, which
        /// lengthens the entry's hold by the same amount.</summary>
        public const float EarthLeadSeconds = 0.55f * Tempo;
        public const float EarthStaggerSeconds = 0.09f * Tempo;

        /// <summary>The healing sparkles reach the first target this long after the reveal card is
        /// gone, and each further target this long after the one before.</summary>
        public const float HealLeadSeconds = 0.42f * Tempo;

        /// <summary>The bolt strikes the first target this long after the reveal card is gone,
        /// and each further target this long after the one before.</summary>
        public const float LightningLeadSeconds = 0.32f * Tempo;
        public const float LightningStaggerSeconds = 0.07f * Tempo;
        private const float ArcSeconds = 0.26f * Tempo;      // ends just after the strike
        private const float StrikeSeconds = 0.32f * Tempo;   // starts just before it
        // The strike's tip sits this far below the frame's centre, as a fraction of its height,
        // so it is the tip that lands on the target.
        private const float StrikeTipOffset = 0.43f;
        /// <summary>The fireball reaches the first target this long after the reveal card is
        /// gone, and each further target this long after the one before.</summary>
        public const float FireLeadSeconds = 0.5f * Tempo;
        public const float FireStaggerSeconds = 0.08f * Tempo;
        private const float FlareSeconds = 0.42f * Tempo;
        private const float FireballSeconds = 0.3f * Tempo;
        private const float FireImpactSeconds = 0.4f * Tempo;

        /// <summary>The shadow reaches the first target this long after the reveal card is gone,
        /// and each further target this long after the one before.</summary>
        public const float ShadowLeadSeconds = 0.5f * Tempo;
        public const float ShadowStaggerSeconds = 0.1f * Tempo;
        private const float ShadowTravelSeconds = 0.4f * Tempo;
        private const float ShadowImpactSeconds = 0.5f * Tempo;
        /// <summary>The black wipe across the caster's portrait takes this long, and finishes as
        /// the first wisp leaves.</summary>
        public const float ShadowWipeSeconds = 0.4f * Tempo;

        // The head of a fireball or shadow wisp sits right of the frame's centre, its trail to the
        // left; this is its offset as a fraction of the drawn width, so the head, not the middle
        // of the trail, is what lands.
        private const float ProjectileHeadOffset = 0.28f;

        /// <summary>The parts reach the first target this long after the reveal card is gone,
        /// and each further target this long after the one before.</summary>
        public const float ClockwrightLeadSeconds = 0.5f * Tempo;
        public const float ClockwrightStaggerSeconds = 0.08f * Tempo;
        private const float ClockwrightTravelSeconds = 0.32f * Tempo;
        private const float ClockwrightTargetSeconds = 0.45f * Tempo;
        // The caster effect, Clockwright's gathering gears or Tempest's charge, runs this long
        // and peaks as the first missile leaves.
        private const float GatherSeconds = 0.5f * Tempo;

        /// <summary>Tidecaller's two schools. Water throws a wave and splashes; frost throws a
        /// shard and lands no effect of its own, because the freeze's crystals are the target
        /// effect (Sam, 2026-10-14). A drowned character gets the wash instead of the splash.</summary>
        public const float WaterLeadSeconds = 0.5f * Tempo;
        public const float WaterStaggerSeconds = 0.08f * Tempo;
        private const float WaterTravelSeconds = 0.34f * Tempo;
        private const float WaterSplashSeconds = 0.45f * Tempo;
        private const float DrownWashSeconds = 0.6f * Tempo;
        public const float FrostLeadSeconds = 0.45f * Tempo;
        public const float FrostStaggerSeconds = 0.07f * Tempo;
        private const float FrostTravelSeconds = 0.26f * Tempo;
        // The frost shard's point is at the frame's right edge, further out than a ball's head.
        private const float FrostHeadOffset = 0.4f;

        /// <summary>Gravekeep: grave mist and bone rise on the caster, a skull trailing green
        /// smoke goes to each character it damages, and soulfire bursts with bone shards there.</summary>
        public const float GravekeepLeadSeconds = 0.5f * Tempo;
        public const float GravekeepStaggerSeconds = 0.09f * Tempo;
        private const float GravekeepTravelSeconds = 0.36f * Tempo;
        private const float GravekeepTargetSeconds = 0.5f * Tempo;
        // The skull sits at the frame's right edge, the smoke trailing behind it.
        private const float SkullHeadOffset = 0.38f;

        /// <summary>Verdant: vines twirl up on the caster, a vine with sprouting leaves reaches
        /// each character it damages, and a wreath of vines closes around it there. A healed
        /// character keeps the healing shimmer instead.</summary>
        public const float VerdantLeadSeconds = 0.5f * Tempo;
        public const float VerdantStaggerSeconds = 0.08f * Tempo;
        private const float VerdantTravelSeconds = 0.36f * Tempo;
        private const float VerdantTargetSeconds = 0.55f * Tempo;
        // The vine's curled tip sits right of centre, the leafy trail behind it.
        private const float VineHeadOffset = 0.33f;
        public const float HealStaggerSeconds = 0.05f * Tempo;
        private const float HealTravelSeconds = 0.36f * Tempo;
        // The lead star sits right of the frame's centre; this is its offset as a fraction of the
        // drawn width, so the star, not the middle of the trail, is what arrives.
        private const float HealHeadOffset = 0.27f;

        private const int SortOrder = 82;
        private const float DepthFraction = 0.06f;
        private const float BuildupSeconds = 0.62f * Tempo;
        private const float FallSeconds = 0.34f * Tempo;
        private const float ImpactSeconds = 0.36f * Tempo;
        private const float ShimmerSeconds = 0.55f * Tempo;
        private const float PoofSeconds = 0.5f;

        private static readonly Dictionary<Sprite, Sprite[]> Frames = new();

        /// <summary>The buildup runs on the caster while the reveal card collapses, finishing as
        /// the first rock starts to fall.</summary>
        public static IEnumerator EarthCaster(Transform parent, Sprite sheet, Camera camera,
                                              Transform caster, float startIn)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null || caster == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "EarthCasterBuildup");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < BuildupSeconds && caster != null && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / BuildupSeconds);
                Show(sr, frames, t);
                Place(sr, camera, caster.position + Vector3.down * BoardController.CardH * 0.22f,
                      BoardController.CardW * Mathf.Lerp(1.6f, 1.9f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 5f) * (1f - Ramp(t, 0.8f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>One rock dropping on one target, landing <paramref name="landIn"/> seconds
        /// from now, then the impact where it lands.</summary>
        public static IEnumerator EarthRock(Transform parent, Sprite rockSheet, Sprite impactSheet,
                                            Camera camera, Transform target, Vector3 fallback,
                                            float landIn)
        {
            float wait = landIn - FallSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || rockSheet == null || camera == null) yield break;

            Vector3 Target() => target != null ? target.position : fallback;

            // Down the screen and slightly toward the viewer, so it reads as falling onto the
            // board rather than sliding across it.
            Vector3 drop = camera.transform.up * BoardController.CardH * 2.4f;
            SpriteRenderer rock = MakeRenderer(parent, "EarthFallingRock");
            Sprite[] frames = FramesOf(rockSheet);
            float fall = Mathf.Min(FallSeconds, Mathf.Max(0.01f, landIn));
            float elapsed = 0f;
            while (elapsed < fall && rock != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fall);
                // The three drawings tumble the rock; they loop rather than play once.
                SpriteMaterials.Set(rock, frames[Mathf.FloorToInt(elapsed / 0.07f) % frames.Length]);
                float eased = t * t; // gravity: slow off the top, fastest on landing
                Place(rock, camera, Target() + drop * (1f - eased),
                      BoardController.CardW * Mathf.Lerp(0.95f, 1.15f, eased));
                rock.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 6f));
                yield return null;
            }
            if (rock != null) Object.Destroy(rock.gameObject);
            if (parent == null || impactSheet == null) yield break;

            SpriteRenderer impact = MakeRenderer(parent, "EarthImpact");
            Sprite[] burst = FramesOf(impactSheet);
            elapsed = 0f;
            while (elapsed < ImpactSeconds && impact != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / ImpactSeconds);
                Show(impact, burst, t);
                Place(impact, camera, Target(), BoardController.CardW * Mathf.Lerp(1.3f, 1.9f, t));
                impact.color = new Color(1f, 1f, 1f, 1f - Ramp(t, 0.6f));
                yield return null;
            }
            if (impact != null) Object.Destroy(impact.gameObject);
        }

        /// <summary>The shimmer opens on each healed target as the healing lands, a beat apart
        /// when there are several.</summary>
        public static IEnumerator HealShimmer(Transform parent, Sprite sheet, Camera camera,
                                              Transform target, Vector3 fallback, float startIn)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "HealShimmer");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < ShimmerSeconds && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / ShimmerSeconds);
                Show(sr, frames, t);
                Place(sr, camera, target != null ? target.position : fallback,
                      BoardController.CardW * Mathf.Lerp(1.0f, 1.35f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 8f) * (1f - Ramp(t, 0.65f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>The poof bursts over a minion the moment a Venom source's hit lands on it,
        /// which for a combat hit is when the lunge connects, the same delay as the damage
        /// number.</summary>
        public static IEnumerator VenomPoof(Transform parent, Sprite sheet, Camera camera,
                                            Transform target, float startIn)
        {
            if (target == null) yield break;
            Vector3 last = target.position;
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "VenomHitPoof");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < PoofSeconds && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / PoofSeconds);
                if (target != null) last = target.position;
                Show(sr, frames, t);
                // Not a spell effect: Place enlarges by Size, and the Venom poof keeps its own size.
                Place(sr, camera, last, BoardController.CardW * Mathf.Lerp(1.1f, 1.6f, t) / Size);
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 10f) * (1f - Ramp(t, 0.6f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>When the <paramref name="index"/>th rock lands, measured from the reveal's
        /// start: after the card, then a stagger apart. The last lands on the release.</summary>
        public static float RockLandsAt(float cardSeconds, int index) =>
            cardSeconds + EarthLeadSeconds + index * EarthStaggerSeconds;

        /// <summary>Which way each Tempest spell's lightning travels: down from the sky, across
        /// the board from the caster, or (Chain Lightning) hopping target to target. Keyed by card
        /// id because the card carries no such field; a Tempest spell not listed here crosses.
        /// Lightning Bolt comes from above (Sam, 2026-10-14); the rest are his to sort.</summary>
        public static LightningShape LightningShapeOf(int cardId) => cardId switch
        {
            185 => LightningShape.FromAbove, // Lightning Bolt
            186 => LightningShape.Chain,     // Chain Lightning
            _   => LightningShape.Across,
        };

        public static float LightningStrikesAt(float cardSeconds, int index) =>
            cardSeconds + LightningLeadSeconds + index * LightningStaggerSeconds;

        /// <summary>An arc from the caster to one target, then the bolt down onto it, striking
        /// <paramref name="strikeIn"/> seconds from now.</summary>
        public static IEnumerator LightningStrike(Transform parent, Sprite arcSheet, Sprite strikeSheet,
                                                  Camera camera, Transform caster, Transform target,
                                                  Vector3 fallback, float strikeIn)
        {
            float arcStart = strikeIn - ArcSeconds * 0.8f;
            if (arcStart > 0f) yield return new WaitForSeconds(arcStart);
            if (parent == null || camera == null) yield break;

            Vector3 Target() => target != null ? target.position : fallback;
            SpriteRenderer arc = caster != null && arcSheet != null && caster != target
                ? MakeRenderer(parent, "LightningArc") : null;
            SpriteRenderer bolt = null;
            Sprite[] arcFrames = arcSheet != null ? FramesOf(arcSheet) : null;
            Sprite[] boltFrames = strikeSheet != null ? FramesOf(strikeSheet) : null;
            float boltAt = ArcSeconds * 0.8f - 0.06f;
            float elapsed = 0f;
            while (elapsed < boltAt + StrikeSeconds)
            {
                if (parent == null) yield break;
                elapsed += Time.deltaTime;

                if (arc != null)
                {
                    float t = Mathf.Clamp01(elapsed / ArcSeconds);
                    if (t >= 1f) { Object.Destroy(arc.gameObject); arc = null; }
                    else
                    {
                        // The bolt flickers between its three drawings rather than playing them once.
                        SpriteMaterials.Set(arc, arcFrames[Mathf.FloorToInt(elapsed / 0.05f) % arcFrames.Length]);
                        Stretch(arc, camera, caster != null ? caster.position : Target(), Target());
                        arc.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 8f) * (1f - Ramp(t, 0.7f)));
                    }
                }

                if (elapsed >= boltAt && boltFrames != null)
                {
                    bolt ??= MakeRenderer(parent, "LightningStrike");
                    float t = Mathf.Clamp01((elapsed - boltAt) / StrikeSeconds);
                    SpriteMaterials.Set(bolt, boltFrames[Mathf.FloorToInt((elapsed - boltAt) / 0.06f) % boltFrames.Length]);
                    float height = BoardController.CardH * 2.1f;
                    Place(bolt, camera, Target() + Vector3.up * (height * StrikeTipOffset), height);
                    // It cracks down from above in the first few frames, then holds and fades.
                    bolt.transform.localScale = new Vector3(bolt.transform.localScale.x,
                        bolt.transform.localScale.y * Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(t * 5f)), 1f);
                    bolt.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 12f) * (1f - Ramp(t, 0.5f)));
                }
                yield return null;
            }
            if (arc != null) Object.Destroy(arc.gameObject);
            if (bolt != null) Object.Destroy(bolt.gameObject);
        }

        // Lays a horizontal frame along from..to: its painted span (about 0.85 of the frame) covers
        // the distance, and it keeps a fixed thickness however far apart the two ends are.
        private static void Stretch(SpriteRenderer sr, Camera camera, Vector3 from, Vector3 to)
        {
            if (sr.sprite == null) return;
            Vector3 a = Vector3.Lerp(from, camera.transform.position, DepthFraction);
            Vector3 b = Vector3.Lerp(to, camera.transform.position, DepthFraction);
            Vector3 delta = b - a;
            Vector3 extent = sr.sprite.bounds.size;
            sr.transform.position = (a + b) * 0.5f;
            sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            sr.transform.localScale = new Vector3(
                delta.magnitude / 0.85f / Mathf.Max(extent.x, 0.001f),
                BoardController.CardW * 0.9f * Size / Mathf.Max(extent.y, 0.001f), 1f);
        }

        public static float HealArrivesAt(float cardSeconds, int index) =>
            cardSeconds + HealLeadSeconds + index * HealStaggerSeconds;

        /// <summary>A trail of sparkles from the caster to one healed target, its lead star
        /// arriving <paramref name="arriveIn"/> seconds from now.</summary>
        public static IEnumerator HealTravel(Transform parent, Sprite sheet, Camera camera,
                                             Transform from, Transform target, Vector3 fallback,
                                             float arriveIn)
        {
            float travel = Mathf.Min(HealTravelSeconds, Mathf.Max(0.01f, arriveIn));
            float wait = arriveIn - travel;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || sheet == null || camera == null || from == null) yield break;

            Vector3 start = from.position;
            SpriteRenderer sr = MakeRenderer(parent, "HealTravel");
            Sprite[] frames = FramesOf(sheet);
            float width = BoardController.CardW * 1.8f;
            float elapsed = 0f;
            while (elapsed < travel && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / travel);
                Vector3 end = target != null ? target.position : fallback;
                Vector3 delta = end - start;
                Vector3 dir = delta.sqrMagnitude > 1e-6f ? delta.normalized : Vector3.right;
                Vector3 head = Vector3.Lerp(start, end, Mathf.SmoothStep(0f, 1f, t));

                SpriteMaterials.Set(sr, frames[Mathf.FloorToInt(elapsed / 0.08f) % frames.Length]);
                Place(sr, camera, head - dir * (width * HealHeadOffset), width);
                sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 6f) * (1f - Ramp(t, 0.85f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        public static float FireballLandsAt(float cardSeconds, int index) =>
            cardSeconds + FireLeadSeconds + index * FireStaggerSeconds;

        /// <summary>The flare opens on the caster as the card collapses and peaks as the first
        /// fireball leaves.</summary>
        public static float FlareStartsAt(float cardSeconds) =>
            Mathf.Max(0f, cardSeconds + FireLeadSeconds - FireballSeconds - FlareSeconds * 0.8f);

        /// <summary>The flare on the caster: the swirl gathers, the flame rises, and it dies down
        /// as the fireball leaves.</summary>
        public static IEnumerator FireCaster(Transform parent, Sprite sheet, Camera camera,
                                             Transform caster, float startIn)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null || caster == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "FireCasterFlare");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < FlareSeconds && caster != null && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / FlareSeconds);
                Show(sr, frames, t);
                Place(sr, camera, caster.position, BoardController.CardW * Mathf.Lerp(1.3f, 1.7f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 6f) * (1f - Ramp(t, 0.75f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>A fireball from the caster to one target, landing <paramref name="landIn"/>
        /// seconds from now, then the burst where it lands.</summary>
        public static IEnumerator Fireball(Transform parent, Sprite ballSheet, Sprite burstSheet,
                                           Camera camera, Transform from, Transform target,
                                           Vector3 fallback, float landIn) =>
            Projectile("Fire", FireballSeconds, FireImpactSeconds, 0.06f, ProjectileHeadOffset, parent,
                       ballSheet, burstSheet, camera, from, target, fallback, landIn);

        public static float GravekeepLandsAt(float cardSeconds, int index) =>
            cardSeconds + GravekeepLeadSeconds + index * GravekeepStaggerSeconds;

        /// <summary>A skull from the caster to one target, then the soulfire where it lands.</summary>
        public static IEnumerator SkullBolt(Transform parent, Sprite skullSheet, Sprite burstSheet,
                                            Camera camera, Transform from, Transform target,
                                            Vector3 fallback, float landIn) =>
            Projectile("Gravekeep", GravekeepTravelSeconds, GravekeepTargetSeconds, 0.09f, SkullHeadOffset,
                       parent, skullSheet, burstSheet, camera, from, target, fallback, landIn);

        public static float VerdantLandsAt(float cardSeconds, int index) =>
            cardSeconds + VerdantLeadSeconds + index * VerdantStaggerSeconds;

        /// <summary>A vine from the caster to one target, then the wreath that closes on it.</summary>
        public static IEnumerator VineBolt(Transform parent, Sprite vineSheet, Sprite wreathSheet,
                                           Camera camera, Transform from, Transform target,
                                           Vector3 fallback, float landIn) =>
            Projectile("Verdant", VerdantTravelSeconds, VerdantTargetSeconds, 0.09f, VineHeadOffset,
                       parent, vineSheet, wreathSheet, camera, from, target, fallback, landIn);

        public static float WaterLandsAt(float cardSeconds, int index) =>
            cardSeconds + WaterLeadSeconds + index * WaterStaggerSeconds;

        public static float FrostLandsAt(float cardSeconds, int index) =>
            cardSeconds + FrostLeadSeconds + index * FrostStaggerSeconds;

        /// <summary>A wave from the caster to one target, then the splash where it lands, or the
        /// longer wash for a character the spell drowns.</summary>
        public static IEnumerator WaterBolt(Transform parent, Sprite waveSheet, Sprite landSheet,
                                            bool drowns, Camera camera, Transform from,
                                            Transform target, Vector3 fallback, float landIn) =>
            Projectile("Water", WaterTravelSeconds, drowns ? DrownWashSeconds : WaterSplashSeconds,
                       0.08f, ProjectileHeadOffset, parent, waveSheet, landSheet, camera, from,
                       target, fallback, landIn);

        /// <summary>An ice shard from the caster to one target. No impact: the freeze's own
        /// crystals, or the damage, are what it lands as.</summary>
        public static IEnumerator FrostBolt(Transform parent, Sprite shardSheet, Camera camera,
                                            Transform from, Transform target, Vector3 fallback,
                                            float landIn) =>
            Projectile("Frost", FrostTravelSeconds, 0f, 0.07f, FrostHeadOffset, parent, shardSheet,
                       null, camera, from, target, fallback, landIn);

        public static float ShadowWipeStartsAt(float cardSeconds) =>
            Mathf.Max(0f, cardSeconds + ShadowLeadSeconds - ShadowTravelSeconds - ShadowWipeSeconds);

        public static float ShadowLandsAt(float cardSeconds, int index) =>
            cardSeconds + ShadowLeadSeconds + index * ShadowStaggerSeconds;

        /// <summary>A wisp of shadow from the caster to one target, then the ring that blooms
        /// where it lands. Slower than the fireball: it drifts rather than flies.</summary>
        public static IEnumerator ShadowBolt(Transform parent, Sprite wispSheet, Sprite burstSheet,
                                             Camera camera, Transform from, Transform target,
                                             Vector3 fallback, float landIn) =>
            Projectile("Shadow", ShadowTravelSeconds, ShadowImpactSeconds, 0.09f, ProjectileHeadOffset,
                       parent, wispSheet, burstSheet, camera, from, target, fallback, landIn);

        // A missile from the caster to one target, landing landIn seconds from now, then the burst
        // where it lands. With no caster on the board it bursts without the flight. Every sheet
        // draws the head right of centre, its trail to the left; headOffset is how far right.
        private static IEnumerator Projectile(string school, float travelSeconds, float impactSeconds,
                                              float frameSeconds, float headOffset, Transform parent,
                                              Sprite ballSheet,
                                              Sprite burstSheet, Camera camera, Transform from,
                                              Transform target, Vector3 fallback, float landIn)
        {
            float travel = Mathf.Min(travelSeconds, Mathf.Max(0.01f, landIn));
            float wait = landIn - travel;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || camera == null) yield break;

            Vector3 Target() => target != null ? target.position : fallback;

            if (from != null && from != target && ballSheet != null)
            {
                Vector3 start = from.position;
                SpriteRenderer ball = MakeRenderer(parent, school + "Projectile");
                Sprite[] frames = FramesOf(ballSheet);
                float width = BoardController.CardW * 1.7f;
                float elapsed = 0f;
                while (elapsed < travel && ball != null)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / travel);
                    Vector3 end = Target();
                    Vector3 delta = end - start;
                    Vector3 dir = delta.sqrMagnitude > 1e-6f ? delta.normalized : Vector3.right;
                    Vector3 head = Vector3.Lerp(start, end, t * t * (3f - 2f * t) * 0.4f + t * 0.6f);

                    // The three drawings churn the trail; they loop rather than play once.
                    SpriteMaterials.Set(ball, frames[Mathf.FloorToInt(elapsed / frameSeconds) % frames.Length]);
                    Place(ball, camera, head - dir * (width * headOffset), width);
                    ball.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                    ball.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 8f));
                    yield return null;
                }
                if (ball != null) Object.Destroy(ball.gameObject);
            }
            if (parent == null || burstSheet == null) yield break;

            SpriteRenderer burst = MakeRenderer(parent, school + "Impact");
            Sprite[] blast = FramesOf(burstSheet);
            float since = 0f;
            while (since < impactSeconds && burst != null)
            {
                since += Time.deltaTime;
                float t = Mathf.Clamp01(since / impactSeconds);
                Show(burst, blast, t);
                Place(burst, camera, Target(), BoardController.CardW * Mathf.Lerp(1.2f, 1.8f, t));
                burst.color = new Color(1f, 1f, 1f, 1f - Ramp(t, 0.55f));
                yield return null;
            }
            if (burst != null) Object.Destroy(burst.gameObject);
        }

        public static float ClockwrightLandsAt(float cardSeconds, int index) =>
            cardSeconds + ClockwrightLeadSeconds + index * ClockwrightStaggerSeconds;

        /// <summary>A spray of parts from the caster to one target it damages, then the burst.</summary>
        public static IEnumerator ClockwrightBolt(Transform parent, Sprite partsSheet, Sprite burstSheet,
                                                  Camera camera, Transform from, Transform target,
                                                  Vector3 fallback, float landIn) =>
            Projectile("Clockwright", ClockwrightTravelSeconds, ClockwrightTargetSeconds, 0.07f,
                       ProjectileHeadOffset, parent, partsSheet, burstSheet, camera, from, target,
                       fallback, landIn);

        /// <summary>When a caster effect of <see cref="GatherSeconds"/> starts so it peaks as the
        /// first missile of a school with this lead leaves, about a third of a second later.</summary>
        public static float GatherStartsAt(float cardSeconds, float lead) =>
            Mathf.Max(0f, cardSeconds + lead - 0.32f * Tempo - GatherSeconds * 0.8f);

        /// <summary>A school's caster effect (Clockwright's gears, Tempest's charge, Tidecaller's
        /// water or frost): plays the three frames once, growing a little, and fades out at the
        /// end. Tempest's sits at the hero's feet, so it reads as gathering from the ground.</summary>
        public static IEnumerator Gather(Transform parent, Sprite sheet, Camera camera, Transform caster,
                                         float startIn, string name, bool atFeet = false)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null || caster == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, name);
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < GatherSeconds && caster != null && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / GatherSeconds);
                Show(sr, frames, t);
                Vector3 at = atFeet ? caster.position + Vector3.down * BoardController.CardH * 0.35f
                                    : caster.position;
                Place(sr, camera, at, BoardController.CardW * Mathf.Lerp(1.4f, 1.75f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 6f) * (1f - Ramp(t, 0.75f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>A school's target effect on its own, for a character the spell acted on
        /// without damaging: buffed, bounced, destroyed and the rest. Starts
        /// <paramref name="startIn"/> seconds from now.</summary>
        public static IEnumerator Burst(Transform parent, Sprite sheet, Camera camera, Transform target,
                                        Vector3 fallback, float startIn)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "SchoolBurst");
            Sprite[] frames = FramesOf(sheet);
            Vector3 last = target != null ? target.position : fallback;
            float elapsed = 0f;
            while (elapsed < BurstSeconds && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / BurstSeconds);
                if (target != null) last = target.position;
                Show(sr, frames, t);
                Place(sr, camera, last, BoardController.CardW * Mathf.Lerp(1.2f, 1.7f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 10f) * (1f - Ramp(t, 0.55f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        private const float BurstSeconds = 0.45f * Tempo;

        public static float BuildupStartsAt(float cardSeconds) =>
            Mathf.Max(0f, cardSeconds + EarthLeadSeconds - FallSeconds - BuildupSeconds * 0.85f);

        // Plays the three frames once over t = 0..1.
        private static void Show(SpriteRenderer sr, Sprite[] frames, float t) =>
            SpriteMaterials.Set(sr, frames[Mathf.Min(frames.Length - 1, Mathf.FloorToInt(t * frames.Length))]);

        private static float Ramp(float t, float from) => Mathf.Clamp01((t - from) / (1f - from));

        // Pulled a little toward the camera, as the hero power effects are, so the effect draws in
        // front of the card it lands on; sized by the larger side of the frame.
        private static void Place(SpriteRenderer sr, Camera camera, Vector3 at, float worldSize)
        {
            if (sr.sprite == null) return;
            sr.transform.position = Vector3.Lerp(at, camera.transform.position, DepthFraction);
            Vector3 extent = sr.sprite.bounds.size;
            sr.transform.localScale = Vector3.one * (worldSize * Size / Mathf.Max(extent.x, extent.y, 0.001f));
        }

        private static SpriteRenderer MakeRenderer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = SortOrder;
            return sr;
        }

        private static Sprite[] FramesOf(Sprite sheet)
        {
            if (Frames.TryGetValue(sheet, out Sprite[] cached)) return cached;
            Rect full = sheet.textureRect;
            var frames = new Sprite[3];
            for (int i = 0; i < frames.Length; i++)
            {
                var rect = Rect.MinMaxRect(
                    full.x + Mathf.Floor(i * full.width / frames.Length), full.y,
                    full.x + Mathf.Floor((i + 1) * full.width / frames.Length), full.y + full.height);
                frames[i] = Sprite.Create(sheet.texture, rect, new Vector2(0.5f, 0.5f), 100f);
            }
            Frames[sheet] = frames;
            return frames;
        }
    }
}
