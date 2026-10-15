using System;
using System.IO;
using System.Reflection;
using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Renders one board minion per status keyword — Aegis, Bulwark, Shroud, Frost, Venom — with
    /// that keyword's treatment SETTLED, as the pictures the public keyword glossary shows
    /// (web/landing, Sam 2026-10-10). Each card is a real one built by BoardController's own
    /// CreatePresenter path, the same way <see cref="CardFrostPreview"/> builds its card.
    ///
    /// WHY THE HARNESS REACHES INTO THE BINDER. Every one of these treatments arrives over time:
    /// the Aegis dome inflates, the smoke and the crystals grow in from an entrance of 0, and the
    /// frost amount tweens up. None of that advances outside play mode — a coroutine started here
    /// runs to its first yield and stops, and Update never ticks — so a card armed the normal way
    /// is captured at the START of each animation, which is invisible. The settled pose is set by
    /// reflection instead. That makes this a picture of where each effect comes to rest, not a
    /// test that the game gets there; CardMaterialBinderVerify covers the wiring.
    ///
    /// Enters no play mode and leaves no scene objects behind.
    /// </summary>
    public static class KeywordStatePreview
    {
        private static string OutDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/keyword-preview"));

        private const float PxPerUnit = 1080f / 9.2f;   // as CardFrostPreview: BoardCamera ortho 4.6
        private const int Scale = 4;

        // Wider than CardFrostPreview's view: the dome, the smoke and the crystals all reach
        // past the card's edge, and a crop that clips them is the wrong picture.
        private const float ViewW = 2.10f;
        private const float ViewH = 2.60f;

        private const float ShroudAmount = 0.60f;       // CardMaterialBinder.ShroudTarget
        private const float ShroudPoseTime = 1.3f;      // any fixed time: the smoke only breathes

        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [MenuItem("Kingdom/Preview/Keyword States")]
        public static void Render()
        {
            Directory.CreateDirectory(OutDir);

            // One shipped minion per keyword, chosen because it carries that keyword and no other.
            Shot("aegis", 1013, 4, 3, ci => ci.HasAegisShield = true, SettleAegis);        // Aegis Templar
            Shot("bulwark", 1007, 2, 5, ci => ci.Keywords.Add(Keyword.BULWARK), null);     // Stone Sentry
            Shot("shroud", 1014, 3, 3, ci => ci.Keywords.Add(Keyword.SHROUD), SettleShroud); // Veilstalker
            // Frost is something done TO a minion, so the card is one that does not print it.
            Shot("frost", 1003, 4, 4, null, SettleFrost);                                 // Cinder Drake
            Shot("venom", 1098, 4, 5, ci => ci.Keywords.Add(Keyword.VENOM), SettleVenom);  // Venomscale Drake

            Debug.Log($"[KeywordStatePreview] wrote 5 PNGs to {OutDir}");
        }

        private static void Shot(string name, int cardId, int attack, int health,
                                 Action<CardInstance> arm, Action<CardMaterialBinder> settle)
        {
            var root = new GameObject("__KeywordStatePreviewRoot");
            try
            {
                AddBackdrop(root.transform);

                var host = new GameObject("__PreviewBoardController");
                host.transform.SetParent(root.transform, false);
                var p0 = new PlayerId("preview-p0");
                var p1 = new PlayerId("preview-p1");
                var board = host.AddComponent<BoardController>();
                board.Initialize(CardDatabaseLoader.Load(), null, p0, p1);

                var ci = new CardInstance(new InstanceId(1), cardId, p0)
                {
                    Zone = Zone.Board,
                    CurrentAttack = attack,
                    CurrentHealth = health,
                    MaxHealth = health,
                };
                arm?.Invoke(ci);

                CardPresenter presenter = board.EditorBuildCard(ci, p0, Zone.Board);
                if (presenter == null)
                {
                    Debug.LogError($"[KeywordStatePreview] could not build a presenter for '{cardId}'.");
                    return;
                }

                var binder = presenter.GetComponentInChildren<CardMaterialBinder>(true);
                if (binder == null)
                {
                    Debug.LogError("[KeywordStatePreview] the card has no CardMaterialBinder.");
                    return;
                }
                binder.SetBoardStatusEffectsVisible(true);
                settle?.Invoke(binder);

                Capture(root.transform, presenter.transform.position, Path.Combine(OutDir, name + ".png"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ── Settling: each jumps one treatment to the end of its entrance ──────────────

        private static void SettleAegis(CardMaterialBinder binder)
        {
            var dome = Field<Renderer>(binder, "_aegisRenderer");
            if (dome == null) { Debug.LogError("[KeywordStatePreview] no aegis renderer."); return; }
            dome.enabled = true;
            Call(binder, "CaptureAegisBase");
            Call(binder, "SetAegisLook", 1f, 1f, 1f);
        }

        private static void SettleShroud(CardMaterialBinder binder)
        {
            Call(binder, "PushFloat", Shader.PropertyToID("_ShroudAmount"), ShroudAmount);
            var smoke = Field<ShroudSmokeView>(binder, "_shroudSmoke");
            if (smoke == null) { Debug.LogError("[KeywordStatePreview] no shroud smoke view."); return; }
            smoke.SetVisible(true);
            Call(smoke, "ApplyPose", 1f, ShroudPoseTime);
        }

        private static void SettleFrost(CardMaterialBinder binder)
        {
            var ice = Field<Renderer>(binder, "_frostRenderer");
            if (ice != null)
            {
                ice.enabled = true;
                var mpb = new MaterialPropertyBlock();
                ice.GetPropertyBlock(mpb);
                mpb.SetFloat(Shader.PropertyToID("_FrostAmount"), 1f);
                ice.SetPropertyBlock(mpb);
            }
            var crystals = Field<FrostCrystalView>(binder, "_frostCrystals");
            if (crystals == null) { Debug.LogError("[KeywordStatePreview] no frost crystal view."); return; }
            crystals.SetVisible(true);
            Call(crystals, "ApplyPose", 1f);
        }

        private static void SettleVenom(CardMaterialBinder binder)
        {
            var bubbles = Field<VenomBubbleView>(binder, "_venomBubbles");
            if (bubbles == null) { Debug.LogError("[KeywordStatePreview] no venom bubble view."); return; }
            bubbles.SetVisible(true);
            Call(bubbles, "ApplyPose", 1f, 0f);
        }

        private static T Field<T>(object target, string name) where T : class
        {
            FieldInfo f = target.GetType().GetField(name, Private);
            if (f == null)
            {
                Debug.LogError($"[KeywordStatePreview] {target.GetType().Name} has no field '{name}' — " +
                               "it was renamed; update this harness.");
                return null;
            }
            return f.GetValue(target) as T;
        }

        private static void Call(object target, string name, params object[] args)
        {
            MethodInfo m = target.GetType().GetMethod(name, Private);
            if (m == null)
            {
                Debug.LogError($"[KeywordStatePreview] {target.GetType().Name} has no method '{name}' — " +
                               "it was renamed; update this harness.");
                return;
            }
            m.Invoke(target, args);
        }

        // ── Capture, as CardFrostPreview ───────────────────────────────────────────────

        private static void Capture(Transform parent, Vector3 at, string path)
        {
            int wPx = Mathf.RoundToInt(ViewW * PxPerUnit) * Scale;
            int hPx = Mathf.RoundToInt(ViewH * PxPerUnit) * Scale;

            var camGo = new GameObject("__KeywordPreviewCam");
            camGo.transform.SetParent(parent, false);
            camGo.transform.position = new Vector3(at.x, at.y, at.z - 10f);

            var cam = camGo.AddComponent<Camera>();
            cam.orthographic     = true;
            cam.orthographicSize = ViewH * 0.5f;
            cam.aspect           = ViewW / ViewH;
            cam.clearFlags       = CameraClearFlags.SolidColor;
            cam.backgroundColor  = Color.black;
            cam.nearClipPlane    = 0.1f;
            cam.farClipPlane     = 50f;

            var rt = new RenderTexture(wPx, hPx, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(wPx, hPx, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, wPx, hPx), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(path, tex.EncodeToPNG());

            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(camGo);
        }

        private static void AddBackdrop(Transform parent)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Backgrounds/BoardPlate_Master.png");
            if (tex == null)
            {
                Debug.LogWarning("[KeywordStatePreview] BoardPlate_Master.png missing — renders on black.");
                return;
            }

            var go = new GameObject("__Backdrop");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(0f, 0f, 5f);   // BoardBackdrop.BehindZ
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                      new Vector2(0.5f, 0.5f), tex.height / 9.2f);
            sr.sortingOrder = -1000;                           // BoardBackdrop.SortOrder
        }
    }
}
