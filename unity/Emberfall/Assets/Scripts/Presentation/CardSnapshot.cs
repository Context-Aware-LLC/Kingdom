using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Flattens a card's visual subtree — art, frame, gem, keyword icons and every TMP label at
    /// once — into a single RenderTexture at a caller-chosen resolution.
    ///
    /// WHY A SNAPSHOT. A card is many renderers across several material families — a composite body
    /// quad, an unlit art quad, sprite-default frame/gem/keyword icons, TMP's SDF shader on every
    /// label, plus the glow, aegis and frost overlay quads. Nothing short of rendering it produces
    /// one image of the card, so
    /// every consumer that needs the card AS A SINGLE SURFACE goes through here:
    /// <see cref="CardDeathDissolve"/> needs one surface to burn coherently, and
    /// <see cref="CardFaceBaker"/> needs one texture the collection wall can hand to UI Toolkit as
    /// a backgroundImage. The wall baking the REAL card is what guarantees a wall tile can never
    /// drift from the in-match card — there is only one card front, and this is a photo of it.
    ///
    /// LAYER DISCIPLINE. The capture camera sees only the "CardCapture" layer (slot 8). The card's
    /// subtree is moved onto it, rendered, and moved back inside ONE synchronous call, so the board
    /// camera — which renders at end of frame, long after this returns — never observes the change.
    /// That is deliberately different from a persistent capture layer excluded from the main
    /// camera's culling mask: with a synchronous restore the failure mode of a leaked layer is a
    /// card that keeps rendering normally, rather than a card that silently disappears.
    ///
    /// THE CALLER OWNS THE TEXTURE. RenderTextures are unmanaged; nothing here caches or frees
    /// them. Release + Destroy every target you take.
    /// </summary>
    public static class CardSnapshot
    {
        /// <summary>Layer the capture camera renders, and nothing else does. Slot 8 in TagManager.</summary>
        public const string CaptureLayerName = "CardCapture";

        // Far enough back to clear the card's own z-spread (frame at -0.006, gem/icons at -0.012,
        // aegis at +0.01) with room for a hovered card's forward offset, close enough that the
        // ortho near/far window stays tight.
        private const float CamDistance = 3f;

        private static Camera s_captureCam;

        /// <summary>
        /// Renders <paramref name="visual"/> alone into a fresh <paramref name="width"/> ×
        /// <paramref name="height"/> target, or null if the capture layer is missing.
        ///
        /// Resolution is the ONLY thing the caller picks. The framing is fixed: the camera is sized
        /// from BoardController.CardW/CardH, because what frames the shot is the card's world size,
        /// not how many pixels the consumer wants of it. That is what keeps a 256×361 death burn
        /// and a 276×387 collection face the same picture at two magnifications.
        /// </summary>
        public static RenderTexture Capture(Transform visual, int width, int height)
        {
            if (visual == null || width <= 0 || height <= 0) return null;

            int layer = LayerMask.NameToLayer(CaptureLayerName);
            if (layer < 0)
            {
                Debug.LogWarning($"[CardSnapshot] layer '{CaptureLayerName}' missing from " +
                                 "TagManager — no card can be snapshotted; dying cards will blink " +
                                 "out and collection faces will come back empty.");
                return null;
            }

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();

            // Includes inactive children so the restore is total — the art quad is inactive
            // whenever a card's illustration has not resolved.
            Transform[] nodes = visual.GetComponentsInChildren<Transform>(true);
            var saved = new int[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                saved[i] = nodes[i].gameObject.layer;
                nodes[i].gameObject.layer = layer;
            }

            Camera cam = CaptureCamera(layer);
            cam.transform.SetPositionAndRotation(visual.position - visual.forward * CamDistance, visual.rotation);
            cam.orthographicSize = BoardController.CardH * 0.5f;
            cam.aspect           = BoardController.CardW / BoardController.CardH;
            cam.targetTexture    = rt;
            try
            {
                cam.Render();
            }
            finally
            {
                cam.targetTexture = null;
                // Synchronous restore is the whole safety argument above: nothing else has
                // rendered between the two loops, so no other camera ever saw the capture layer.
                for (int i = 0; i < nodes.Length; i++)
                    if (nodes[i] != null) nodes[i].gameObject.layer = saved[i];
            }
            return rt;
        }

        private static Camera CaptureCamera(int layer)
        {
            if (s_captureCam != null)
            {
                s_captureCam.cullingMask = 1 << layer;
                return s_captureCam;
            }

            var go = new GameObject("__CardSnapshotCapture") { hideFlags = HideFlags.HideAndDontSave };
            s_captureCam = go.AddComponent<Camera>();
            s_captureCam.enabled         = false;   // rendered on demand only, never in the loop
            s_captureCam.orthographic    = true;
            s_captureCam.cullingMask     = 1 << layer;
            s_captureCam.clearFlags      = CameraClearFlags.SolidColor;
            s_captureCam.backgroundColor = Color.clear;
            s_captureCam.nearClipPlane   = 0.05f;
            s_captureCam.farClipPlane    = CamDistance * 2f;
            s_captureCam.allowMSAA       = false;
            s_captureCam.allowHDR        = false;
            return s_captureCam;
        }
    }
}
