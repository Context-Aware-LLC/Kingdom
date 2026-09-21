using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// EF-049 named verify. Gates the two halves of the reveal layer that can be wrong without
    /// anyone noticing on a desktop Editor: the TRIGGER POLICY (which is the only thing standing
    /// between "readable on Android" and "not readable at all") and the KEYWORD GLOSSARY (which
    /// rots the moment someone adds a Keyword enum value).
    ///
    /// <para>Both are exercised through the shipping types — <see cref="HoverRevealModel"/> and
    /// <see cref="CardRevealContent"/> are plain C# with no MonoBehaviour and no clock of their
    /// own precisely so this can run headlessly. The MonoBehaviour layer above them
    /// (CardRevealTrigger / CardRevealPanel) is thin relay and drawing by design; what is asserted
    /// here is everything that carries a decision.</para>
    ///
    /// <para>Registered in <see cref="HeadlessVerify"/>, so <c>pwsh tools/ci/unity-verify.ps1</c>
    /// fails on a regression. Nothing Unity-side is gated in cloud CI — there is no seat on the
    /// runner — so that harness is the only check this code will ever get.</para>
    /// </summary>
    public static class CardRevealVerify
    {
        [MenuItem("Emberfall/Verify Card Reveal (EF-049 trigger + glossary)")]
        public static void Run()
        {
            int failed = 0;
            failed += TouchLongPressReveals();
            failed += TouchHoldRevealIsSticky();
            failed += NewPressOnSameCardDismissesSticky();
            failed += TouchTapDoesNotReveal();
            failed += MousePressNeverArmsHold();
            failed += MouseHoverReveals();
            failed += HoldOutranksHover();
            failed += StaleExitDoesNotCancelLiveHover();
            failed += EveryKeywordHasGlossary();
            failed += ContentUsesLiveStateOverPrint();
            failed += ReadableZonesIncludeHandAndBoard();
            failed += DirectTooltipSuppressesTouch();
            failed += LayoutFillsSafeHeight();
            failed += FocusViewIsWiredToReadableCards();

            if (failed > 0)
                Debug.LogError($"[CardRevealVerify] {failed} check(s) FAILED.");
            else
                Debug.Log("[CardRevealVerify] GREEN — 14/14 checks passed.");
        }

        // ── Trigger policy ──────────────────────────────────────────────────────────

        // THE check for this task. Before EF-049 there was no touch path at all, so this is the
        // assertion that "you can read a card on Android" is true.
        private static int TouchLongPressReveals()
        {
            var m = new HoverRevealModel();
            m.PointerDown(Card(1), HoverRevealModel.PointerKind.Touch, 0f);
            if (m.Visible) return Fail("TouchLongPressReveals", "revealed instantly on press");

            m.Tick(HoverRevealModel.HoldSeconds - 0.01f);
            if (m.Visible) return Fail("TouchLongPressReveals", "revealed before the hold elapsed");

            m.Tick(HoverRevealModel.HoldSeconds);
            if (!m.Visible) return Fail("TouchLongPressReveals", "no reveal after the hold elapsed");
            if (!m.Target.Equals(Card(1)))
                return Fail("TouchLongPressReveals", $"revealed the wrong card ({m.Target.Value})");
            if (m.Source != HoverRevealModel.RevealSource.Hold)
                return Fail("TouchLongPressReveals", $"source was {m.Source}, expected Hold");
            if (!m.Release(HoverRevealModel.HoldSeconds + 0.1f))
                return Fail("TouchLongPressReveals", "release did not report the press as a read");

            return Pass("TouchLongPressReveals");
        }

        // NOR-147. A touch hold-read goes sticky on release: the panel stays up so the player can
        // lift their finger and read at leisure. Cancel() is the dismissal path (press elsewhere).
        private static int TouchHoldRevealIsSticky()
        {
            var m = new HoverRevealModel();
            m.PointerDown(Card(1), HoverRevealModel.PointerKind.Touch, 0f);
            m.Tick(HoverRevealModel.HoldSeconds);
            m.Release(HoverRevealModel.HoldSeconds + 0.1f);

            if (!m.Visible)
                return Fail("TouchHoldRevealIsSticky", "panel hidden after release — expected sticky");
            if (m.Source != HoverRevealModel.RevealSource.Sticky)
                return Fail("TouchHoldRevealIsSticky", $"source was {m.Source} after release, expected Sticky");
            if (!m.Target.Equals(Card(1)))
                return Fail("TouchHoldRevealIsSticky", $"sticky showing card {m.Target.Value}, expected 1");

            // Cancel (player pressed elsewhere) must dismiss.
            m.Cancel();
            if (m.Visible) return Fail("TouchHoldRevealIsSticky", "panel still visible after Cancel");
            return Pass("TouchHoldRevealIsSticky");
        }

        // NOR-147. Pressing the same card again while sticky must clear the reveal so the player
        // can proceed to play the card without the panel blocking their view.
        private static int NewPressOnSameCardDismissesSticky()
        {
            var m = new HoverRevealModel();
            m.PointerDown(Card(2), HoverRevealModel.PointerKind.Touch, 0f);
            m.Tick(HoverRevealModel.HoldSeconds);
            m.Release(HoverRevealModel.HoldSeconds + 0.05f);
            if (!m.Visible) return Fail("NewPressOnSameCardDismissesSticky", "sticky did not open");

            // Second press on same card clears sticky immediately.
            m.PointerDown(Card(2), HoverRevealModel.PointerKind.Touch, HoverRevealModel.HoldSeconds + 0.1f);
            if (m.Visible)
                return Fail("NewPressOnSameCardDismissesSticky", "panel still up after re-pressing same card");
            return Pass("NewPressOnSameCardDismissesSticky");
        }

        // The other half: a tap must stay a tap. If a short press reported itself as a read, every
        // tap-to-play on the phone would be cancelled by the reveal layer.
        private static int TouchTapDoesNotReveal()
        {
            var m = new HoverRevealModel();
            m.PointerDown(Card(1), HoverRevealModel.PointerKind.Touch, 0f);
            m.Tick(HoverRevealModel.HoldSeconds * 0.5f);
            bool read = m.Release(HoverRevealModel.HoldSeconds * 0.5f);

            if (m.Visible) return Fail("TouchTapDoesNotReveal", "a short tap revealed");
            if (read) return Fail("TouchTapDoesNotReveal", "a short tap reported itself as a read");
            return Pass("TouchTapDoesNotReveal");
        }

        // A slow mouse click on desktop must not become a read — it would cancel the selection the
        // same click just made. Desktop reveals on hover and never needs the press.
        private static int MousePressNeverArmsHold()
        {
            var m = new HoverRevealModel();
            m.PointerDown(Card(1), HoverRevealModel.PointerKind.Mouse, 0f);
            m.Tick(HoverRevealModel.HoldSeconds * 4f);

            if (m.Visible) return Fail("MousePressNeverArmsHold", "a held mouse press revealed");
            if (m.PressBecameRead)
                return Fail("MousePressNeverArmsHold", "a held mouse press became a read");
            if (m.Release(HoverRevealModel.HoldSeconds * 4f))
                return Fail("MousePressNeverArmsHold", "release reported a mouse press as a read");
            return Pass("MousePressNeverArmsHold");
        }

        private static int MouseHoverReveals()
        {
            var m = new HoverRevealModel();
            m.PointerEnter(Card(7), 0f);
            m.Tick(HoverRevealModel.HoverDelaySeconds - 0.01f);
            if (m.Visible) return Fail("MouseHoverReveals", "revealed before the hover delay elapsed");

            m.Tick(HoverRevealModel.HoverDelaySeconds);
            if (!m.Visible) return Fail("MouseHoverReveals", "no reveal after the hover delay");
            if (m.Source != HoverRevealModel.RevealSource.Hover)
                return Fail("MouseHoverReveals", $"source was {m.Source}, expected Hover");

            m.PointerExit(Card(7), HoverRevealModel.HoverDelaySeconds + 0.1f);
            if (m.Visible) return Fail("MouseHoverReveals", "still visible after the pointer left");
            return Pass("MouseHoverReveals");
        }

        private static int HoldOutranksHover()
        {
            var m = new HoverRevealModel();
            m.PointerEnter(Card(2), 0f);
            m.Tick(HoverRevealModel.HoverDelaySeconds);
            m.PointerDown(Card(3), HoverRevealModel.PointerKind.Touch, HoverRevealModel.HoverDelaySeconds);
            // Step a hair past the boundary: HoverDelaySeconds + HoldSeconds cannot represent both
            // operands exactly, and subtracting them at the threshold can land one float ULP below
            // HoldSeconds.
            m.Tick(HoverRevealModel.HoverDelaySeconds + HoverRevealModel.HoldSeconds + 0.01f);

            if (m.Source != HoverRevealModel.RevealSource.Hold)
                return Fail("HoldOutranksHover", $"source was {m.Source}, expected Hold to win");
            if (!m.Target.Equals(Card(3)))
                return Fail("HoldOutranksHover", $"showed card {m.Target.Value}, expected 3");
            return Pass("HoldOutranksHover");
        }

        // Unity gives no ordering guarantee between one collider's exit and the next one's enter,
        // so a late exit for the card the pointer already left must not close the live reveal.
        private static int StaleExitDoesNotCancelLiveHover()
        {
            var m = new HoverRevealModel();
            m.PointerEnter(Card(4), 0f);
            m.PointerEnter(Card(5), 0.01f);           // moved on to the neighbour
            m.PointerExit(Card(4), 0.02f);            // card 4's exit lands late
            m.Tick(HoverRevealModel.HoverDelaySeconds + 0.02f);

            if (!m.Visible) return Fail("StaleExitDoesNotCancelLiveHover", "the stale exit closed the reveal");
            if (!m.Target.Equals(Card(5)))
                return Fail("StaleExitDoesNotCancelLiveHover", $"showed card {m.Target.Value}, expected 5");
            return Pass("StaleExitDoesNotCancelLiveHover");
        }

        // ── Content ─────────────────────────────────────────────────────────────────

        // The glossary is the reason the panel is worth building — the card face has room for a
        // keyword's name and never for what it does. A Keyword added without copy would silently
        // print nothing, so this walks the enum rather than a hand-kept list.
        private static int EveryKeywordHasGlossary()
        {
            var missing = new List<string>();
            foreach (Keyword kw in (Keyword[])Enum.GetValues(typeof(Keyword)))
            {
                CardRevealContent c = CardRevealContent.Build(MinionWith(kw), null);
                string name = kw.ToString();
                // Build() drops a keyword whose glossary is null, so an absent NAME in the body is
                // exactly the signal — no need to reach into KeywordCopy from here.
                if (c.Body == null || c.Body.IndexOf(name.Replace('_', ' '),
                        StringComparison.OrdinalIgnoreCase) < 0)
                    missing.Add(name);
            }

            if (missing.Count > 0)
                return Fail("EveryKeywordHasGlossary",
                            $"{missing.Count} keyword(s) with no glossary copy: {string.Join(", ", missing)}");

            Debug.Log($"[CardRevealVerify]   glossary covers all {Enum.GetValues(typeof(Keyword)).Length} Keyword values.");
            return Pass("EveryKeywordHasGlossary");
        }

        // A buffed minion reads nothing like its printed card, and the reveal is what a player uses
        // to judge a trade. Printed stats here would be confidently wrong.
        private static int ContentUsesLiveStateOverPrint()
        {
            CardDefinition def = MinionWith(Keyword.BULWARK, attack: 2, health: 3);
            var live = new StubLive { Attack = 5, Health = 1 };

            CardRevealContent c = CardRevealContent.Build(def, live);
            if (c.Subtitle == null || c.Subtitle.IndexOf("5 / 1", StringComparison.Ordinal) < 0)
                return Fail("ContentUsesLiveStateOverPrint", $"live stats missing from \"{c.Subtitle}\"");
            if (c.Subtitle.IndexOf("printed 2/3", StringComparison.Ordinal) < 0)
                return Fail("ContentUsesLiveStateOverPrint", $"printed stats not annotated in \"{c.Subtitle}\"");

            // Unbuffed: the annotation is noise and must not appear.
            CardRevealContent plain = CardRevealContent.Build(def, new StubLive { Attack = 2, Health = 3 });
            if (plain.Subtitle.IndexOf("printed", StringComparison.Ordinal) >= 0)
                return Fail("ContentUsesLiveStateOverPrint",
                            $"annotated an unbuffed minion: \"{plain.Subtitle}\"");

            return Pass("ContentUsesLiveStateOverPrint");
        }

        private static int ReadableZonesIncludeHandAndBoard()
        {
            if (!CardRevealTrigger.CanInspectZone(Zone.Hand))
                return Fail("ReadableZonesIncludeHandAndBoard", "local hand is not inspectable");
            if (!CardRevealTrigger.CanInspectZone(Zone.Board))
                return Fail("ReadableZonesIncludeHandAndBoard", "board is not inspectable");
            if (CardRevealTrigger.CanInspectZone(Zone.Deck))
                return Fail("ReadableZonesIncludeHandAndBoard", "deck unexpectedly became inspectable");
            return Pass("ReadableZonesIncludeHandAndBoard");
        }

        private static int DirectTooltipSuppressesTouch()
        {
            if (CardPresenter.ShouldShowDirectHover(touchPressed: true))
                return Fail("DirectTooltipSuppressesTouch", "touch would open the sticky on-card tooltip");
            if (!CardPresenter.ShouldShowDirectHover(touchPressed: false))
                return Fail("DirectTooltipSuppressesTouch", "desktop hover tooltip was suppressed");
            return Pass("DirectTooltipSuppressesTouch");
        }

        private static int LayoutFillsSafeHeight()
        {
            // Pixel 5a landscape safe viewport from DeviceLayout's device evidence: left cutout,
            // otherwise full height. Work in normalized screen-plane units so this stays pure.
            var safe = Rect.MinMaxRect(136f / 2400f, 0f, 1f, 1f);
            CardRevealLayout layout = CardRevealLayout.Solve(20f / 9f, 1f, safe);
            if (!Mathf.Approximately(layout.CardHeight, 1f))
                return Fail("LayoutFillsSafeHeight", $"card uses {layout.CardHeight:P0} of safe height");

            float expectedWidth = BoardController.CardW / BoardController.CardH;
            if (!Mathf.Approximately(layout.CardWidth, expectedWidth))
                return Fail("LayoutFillsSafeHeight", "card aspect ratio changed while filling height");

            float safeLeft = -0.5f * (20f / 9f) + 136f / 2400f * (20f / 9f);
            float safeRight = 0.5f * (20f / 9f);
            float left = layout.CardCenter.x - layout.CardWidth * 0.5f;
            float right = layout.CardCenter.x + layout.CardWidth * 0.5f;
            if (left < safeLeft || right > safeRight)
                return Fail("LayoutFillsSafeHeight", $"card [{left:F3}, {right:F3}] escaped safe [{safeLeft:F3}, {safeRight:F3}]");
            return Pass("LayoutFillsSafeHeight");
        }

        private static int FocusViewIsWiredToReadableCards()
        {
            var root = new GameObject("CardFocusVerifyRoot");
            var routerGo = new GameObject("CardFocusVerifyRouter");
            GameObject cameraGo = null;
            CardRevealPanel panel = null;
            try
            {
                if (Camera.main == null)
                {
                    cameraGo = new GameObject("CardFocusVerifyCamera");
                    cameraGo.tag = "MainCamera";
                    cameraGo.AddComponent<Camera>();
                }

                var p0 = new PlayerId("focus-p0");
                var p1 = new PlayerId("focus-p1");
                CardDefinition def = MinionWith(Keyword.BULWARK);
                var db = new InMemoryCardDatabase(new[] { def });
                var router = routerGo.AddComponent<InputRouter>();
                var board = root.AddComponent<BoardController>();
                board.Initialize(db, null, p0, p1, router);

                var card = new CardInstance(new InstanceId(77), def.Id, p0)
                {
                    Zone = Zone.Board,
                    CurrentAttack = def.Attack ?? 0,
                    CurrentHealth = def.Health ?? 0,
                    MaxHealth = def.Health ?? 0,
                };
                CardPresenter presenter = board.EditorBuildCard(card, p0, Zone.Board);
                CardRevealTrigger boardTrigger = presenter != null
                    ? presenter.GetComponent<CardRevealTrigger>()
                    : null;
                var handCard = new CardInstance(new InstanceId(78), def.Id, p0)
                {
                    Zone = Zone.Hand,
                    CurrentAttack = def.Attack ?? 0,
                    CurrentHealth = def.Health ?? 0,
                    MaxHealth = def.Health ?? 0,
                };
                CardPresenter handPresenter = board.EditorBuildCard(handCard, p0, Zone.Hand);
                CardRevealTrigger handTrigger = handPresenter != null
                    ? handPresenter.GetComponent<CardRevealTrigger>()
                    : null;
                CardRevealController controller = root.GetComponentInChildren<CardRevealController>();
                panel = controller != null ? controller.FocusPanel : null;

                if (boardTrigger == null || !boardTrigger.IsInspectable)
                    return Fail("FocusViewIsWiredToReadableCards", "board presenter has no live reveal trigger");
                if (handTrigger == null || !handTrigger.IsInspectable)
                    return Fail("FocusViewIsWiredToReadableCards", "hand presenter has no live reveal trigger");
                if (panel == null)
                    return Fail("FocusViewIsWiredToReadableCards", "board initialization created no focus panel");

                panel.Show(handTrigger);
                CardPresenter[] focused = panel.GetComponentsInChildren<CardPresenter>(true);
                if (focused.Length != 1)
                    return Fail("FocusViewIsWiredToReadableCards",
                        $"focus panel contains {focused.Length} card faces, expected exactly one");
                if (focused[0].GetComponent<BoxCollider>() != null)
                    return Fail("FocusViewIsWiredToReadableCards", "focused copy retained an input collider");
                if (panel.transform.Find("Face") != null
                    || panel.transform.Find("Title") != null
                    || panel.transform.Find("Subtitle") != null
                    || panel.transform.Find("Body") != null)
                    return Fail("FocusViewIsWiredToReadableCards", "secondary detail panel still exists");

                return Pass("FocusViewIsWiredToReadableCards");
            }
            finally
            {
                // The screen-anchored panel re-parents itself to the camera, outside root. Destroy
                // it explicitly so an edit-mode verification can never leave visual debris in
                // the open scene even if MonoBehaviour destruction callbacks are suppressed.
                if (panel != null) UnityEngine.Object.DestroyImmediate(panel.gameObject);
                if (root != null)
                {
                    BoardController board = root.GetComponent<BoardController>();
                    if (board != null && board.FlavorPanel != null)
                        UnityEngine.Object.DestroyImmediate(board.FlavorPanel.gameObject);
                }
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(routerGo);
                if (cameraGo != null) UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────

        private sealed class StubLive : ICardLiveState
        {
            public InstanceId Id { get; set; }
            public int Attack { get; set; }
            public int Health { get; set; }
            public int MaxHealth { get; set; }
            public IReadOnlyCollection<Keyword> Keywords { get; set; } = Array.Empty<Keyword>();
            public AuraSet Auras { get; set; }
            public int BoardSlot { get; set; }
            public AttackReadiness AttackReadiness { get; set; }
#pragma warning disable 67
            public event Action<CardChange> Changed;
#pragma warning restore 67
        }

        private static InstanceId Card(int id) => new InstanceId(id);

        private static CardDefinition MinionWith(Keyword kw, int attack = 1, int health = 1) =>
            new CardDefinition(
                id: 9010, handle: "verify_minion", name: "Verify Minion", type: CardType.MINION, cost: 1,
                attack: attack, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                // `effects:`/`EffectDef` were removed with the legacy effect model (8a86fc0) and
                // `cardText:` with the switch to generated text (eec7c6c). Typed rules replace
                // both; this fixture needs none, so an empty list is the faithful translation.
                keywords: new[] { kw }, rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "verify", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static int Pass(string name)
        {
            Debug.Log($"[CardRevealVerify] PASS {name}");
            return 0;
        }

        private static int Fail(string name, string detail)
        {
            Debug.LogError($"[CardRevealVerify] FAIL {name} — {detail}");
            return 1;
        }
    }
}
