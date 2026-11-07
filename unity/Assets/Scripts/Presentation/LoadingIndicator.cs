using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>StormGlass is the shared loading heartbeat. Both render paths advance only on
    /// Unity's main thread, using unscaled time: a paused game still pulses, a stalled thread does not.</summary>
    public static class LoadingIndicator
    {
        public const string ClassName = "loading-indicator";
        public const string AssetPath = "Assets/Art/Orbs/Orb_StormGlass.png";
        public const string Address = "orbs/Orb_StormGlass";
        private const float PeriodSeconds = 1.6f;
        private static readonly int LoadingTimeId = Shader.PropertyToID("_LoadingTime");
        private static readonly int HeartbeatId = Shader.PropertyToID("_Heartbeat");
        private static readonly int FadeId = Shader.PropertyToID("_Fade");

        public static VisualElement Create()
        {
            var indicator = new VisualElement { pickingMode = PickingMode.Ignore };
            Schedule(indicator);
            return indicator;
        }

        /// <summary>The element owns its schedule, which stops when detached. Callers that keep a
        /// hidden loading overlay attached can pause/resume the returned item with their wait state.</summary>
        public static IVisualElementScheduledItem Schedule(VisualElement indicator)
        {
            indicator.AddToClassList(ClassName);
            indicator.pickingMode = PickingMode.Ignore;
            return indicator.schedule.Execute(() =>
            {
                float pulse = Pulse(Time.unscaledTime);
                float scale = Mathf.Lerp(0.92f, 1f, pulse);
                indicator.style.scale = new Scale(new Vector3(scale, scale, 1f));
                indicator.style.opacity = Mathf.Lerp(0.65f, 1f, pulse);
            }).Every(16);
        }

        /// <summary>Feed the shader explicitly instead of letting its clock animate independently
        /// of the loading screen. The layout scale is immutable, so the pulse cannot accumulate.</summary>
        public static void UpdateWorld(Material material, Transform orb, Vector3 layoutScale, float fade)
        {
            if (material == null || orb == null) return;
            float time = Time.unscaledTime;
            float pulse = Pulse(time);
            float scale = Mathf.Lerp(0.92f, 1f, pulse);
            orb.localScale = Vector3.Scale(layoutScale, new Vector3(scale, scale, 1f));
            material.SetFloat(LoadingTimeId, time);
            material.SetFloat(HeartbeatId, pulse);
            material.SetFloat(FadeId, fade * Mathf.Lerp(0.65f, 1f, pulse));
        }

        private static float Pulse(float time)
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(time * (2f * Mathf.PI / PeriodSeconds));
            return wave * wave * (3f - 2f * wave);
        }
    }
}
