// SGF_DissolveCore — shared dissolve clip + edge-band logic.
// Used by SG_Dissolve (minion death) and SG_Shroud (edge-translucency shimmer).
//
// Shader Graph: Custom Function node, File mode.
//   Source:   Assets/Shaders/Functions/SGF_DissolveCore.hlsl
//   Function: DissolveCore
//
// Inputs  (match Shader Graph node port names exactly):
//   Noise      float   noise texture sample, R channel, 0..1
//   Threshold  float   _DissolveAmount 0→1 (dissolve progress)
//   EdgeWidth  float   constant authored in graph (e.g. 0.08) — width of burn band
// Outputs:
//   ClipValue  float   feed into Alpha Clip threshold (values < 0 are clipped)
//   EdgeMask   float   1 inside the edge band, 0 outside — multiply by EdgeColor in graph

void DissolveCore_float(float Noise, float Threshold, float EdgeWidth,
                        out float ClipValue, out float EdgeMask)
{
    ClipValue = Noise - Threshold;
    // Edge band: pixels that survive the clip but are within EdgeWidth of the dissolve front.
    float edgeFront = step(Threshold, Noise);              // 1 where not yet clipped
    float edgeBack  = step(Noise, Threshold + EdgeWidth);  // 1 below the front + edge band
    EdgeMask = edgeFront * edgeBack;
}

void DissolveCore_half(half Noise, half Threshold, half EdgeWidth,
                       out half ClipValue, out half EdgeMask)
{
    ClipValue = Noise - Threshold;
    half edgeFront = step(Threshold, Noise);
    half edgeBack  = step(Noise, Threshold + EdgeWidth);
    EdgeMask = edgeFront * edgeBack;
}
