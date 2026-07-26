using UnityEngine;

namespace Emberfall.Presentation
{
    // Cached Shader.PropertyToID values for the 4 per-instance MPB shader floats (interfaces.md §5.2).
    // ManaTrayView owns its own local IDs for the mana params (_FillLevel etc.).
    internal static class ShaderIds
    {
        public static readonly int DissolveAmount = Shader.PropertyToID("_DissolveAmount");
        public static readonly int FlashAmount    = Shader.PropertyToID("_FlashAmount");
        public static readonly int FrostAmount    = Shader.PropertyToID("_FrostAmount");
        public static readonly int StealthAmount  = Shader.PropertyToID("_StealthAmount");

        // Not one of the four MPB floats: CardDeathDissolve pushes the flattened card snapshot
        // into SG_Dissolve's albedo slot on a per-death material instance.
        public static readonly int BaseMap        = Shader.PropertyToID("_BaseMap");
        public static readonly int NoiseOffset    = Shader.PropertyToID("_NoiseOffset");
    }
}
