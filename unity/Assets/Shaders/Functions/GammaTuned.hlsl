// GammaTuned — for shaders whose colour maths was tuned by eye while the project was in Gamma.
//
// In Linear a Color property arrives linearised, an sRGB texture samples linearised, and the
// fragment's output is read as linear. Maths written against the old values (a 0.85 specular, a
// 0.55 dim, a lerp between two tints) then lands somewhere else: the mana orbs came out washed
// to pale blue. TunedIn takes a colour back into the space its constants were tuned in, and
// TunedOut returns the result, so the shader looks as it was signed off.
//
// A shader using these declares no [HDR] Color property. In Linear an [HDR] colour reaches the
// GPU raw from a material but linearised from a MaterialPropertyBlock, while a plain one is
// linearised from both (measured with a probe shader, 2026-10-22). TunedIn can only undo one of
// those, so the plain declaration is what makes it right on every path. Values over 1 still work.
//
// Blending still happens in linear, so a translucent edge is not identical. TunedGlowAlpha is the
// one case that has an exact answer: a glow fading out over something dark. There the linear blend
// shows a^(1/2.2) of what Gamma showed, so a soft halo reads wider and its clipped quad edge shows.
#ifndef KINGDOM_GAMMA_TUNED_INCLUDED
#define KINGDOM_GAMMA_TUNED_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

#if defined(UNITY_COLORSPACE_GAMMA)
    #define TunedIn(c)        (c)
    #define TunedOut(c)       (c)
    #define TunedGlowAlpha(a) (a)
#else
    #define TunedIn(c)        LinearToSRGB(c)
    #define TunedOut(c)       SRGBToLinear(c)
    #define TunedGlowAlpha(a) pow(saturate(a), 2.2)
#endif

#endif
