using UnityEngine;

namespace Kingdom.Presentation
{
    // Cached Shader.PropertyToID values for the 4 per-instance MPB shader floats (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.2).
    // ManaTrayView owns its own local IDs for the mana params (_FillLevel etc.).
    internal static class ShaderIds
    {
        public static readonly int DissolveAmount = Shader.PropertyToID("_DissolveAmount");
        public static readonly int FlashAmount    = Shader.PropertyToID("_FlashAmount");
        public static readonly int FrostAmount    = Shader.PropertyToID("_FrostAmount");
        public static readonly int ShroudAmount  = Shader.PropertyToID("_ShroudAmount");

        // SG_Glow's one exposed color (M_Glow.mat default: HDR gold, r1.6 g1.3 b0.2). Driven
        // per-instance so the valid-target cue can use a different color from every other glow
        // signal — see CardMaterialBinder.ApplyGlow.
        public static readonly int GlowColor      = Shader.PropertyToID("_GlowColor");

        // AegisDome colour and intensity, driven per-instance for the gain / break transitions —
        // see CardMaterialBinder.ApplyAegis.
        public static readonly int AegisColor     = Shader.PropertyToID("_AegisColor");
        public static readonly int AegisIntensity = Shader.PropertyToID("_AegisIntensity");

        // Not one of the four MPB floats: CardDeathDissolve pushes the flattened card snapshot
        // into SG_Dissolve's albedo slot on a per-death material instance.
        public static readonly int BaseMap        = Shader.PropertyToID("_BaseMap");
        public static readonly int NoiseOffset    = Shader.PropertyToID("_NoiseOffset");
    }
}
