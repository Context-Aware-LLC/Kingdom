// SGF_Desaturate — BT.709 luminance-weighted desaturation.
// Used by SG_Frost (ice overlay desaturates the card beneath) and SG_Shroud (faded body).
//
// Shader Graph: Custom Function node, File mode.
//   Source:   Assets/Shaders/Functions/SGF_Desaturate.hlsl
//   Function: Desaturate
//
// Inputs  (match Shader Graph node port names exactly):
//   Color      float3  input RGB (e.g. from SG_CardComposite body sample)
//   Strength   float   0 = no change, 1 = full greyscale
//                      For SG_Frost: drive with _FrostAmount * authored constant
//                      For SG_Shroud: drive with _ShroudAmount (already 0..0.6 max)
// Outputs:
//   Result     float3  desaturated RGB

void Desaturate_float(float3 Color, float Strength, out float3 Result)
{
    // BT.709 perceptual luminance weights.
    float lum = dot(Color, float3(0.2126, 0.7152, 0.0722));
    Result = lerp(Color, float3(lum, lum, lum), Strength);
}

void Desaturate_half(half3 Color, half Strength, out half3 Result)
{
    half lum = dot(Color, half3(0.2126h, 0.7152h, 0.0722h));
    Result = lerp(Color, half3(lum, lum, lum), Strength);
}
