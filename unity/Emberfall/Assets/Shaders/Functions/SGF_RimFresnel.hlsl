// SGF_RimFresnel — UV-distance rim falloff with optional sine pulse.
// Used by SG_Glow (playable/buff/Spell Damage rim) and SG_Aegis (Divine Shield bubble).
//
// Shader Graph: Custom Function node, File mode.
//   Source:   Assets/Shaders/Functions/SGF_RimFresnel.hlsl
//   Function: RimFresnel
//
// Inputs  (match Shader Graph node port names exactly):
//   UV         float2  card/quad UV, 0..1, center = (0.5, 0.5)
//   RimWidth   float   fraction of edge that glows (authored constant, e.g. 0.18)
//   PulseSpeed float   sine cycles per second (0 = no pulse; e.g. 1.5 for a slow throb)
//   Time       float   connect to Time node → Time output (_Time.y equivalent)
// Outputs:
//   Rim        float   0 at card center, peaks at edge — multiply by color + alpha in graph

void RimFresnel_float(float2 UV, float RimWidth, float PulseSpeed, float Time,
                      out float Rim)
{
    float2 centered = UV - float2(0.5, 0.5);
    float  dist     = saturate(length(centered) * 2.0);   // 0 = center, 1 = edge corner
    float  falloff  = smoothstep(1.0 - RimWidth, 1.0, dist);
    // Sine pulse: ±25 % amplitude, phase-driven by Time.
    float  pulse    = 1.0 + 0.25 * sin(Time * PulseSpeed * 6.2832);
    Rim = saturate(falloff * pulse);
}

void RimFresnel_half(half2 UV, half RimWidth, half PulseSpeed, half Time,
                     out half Rim)
{
    half2 centered = UV - half2(0.5h, 0.5h);
    half  dist     = saturate(length(centered) * 2.0h);
    half  falloff  = smoothstep(1.0h - RimWidth, 1.0h, dist);
    half  pulse    = 1.0h + 0.25h * sin(Time * PulseSpeed * 6.2832h);
    Rim = saturate(falloff * pulse);
}
