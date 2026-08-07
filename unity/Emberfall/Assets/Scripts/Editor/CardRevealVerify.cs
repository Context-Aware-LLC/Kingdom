using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
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
            failed += TouchTapDoesNotReveal();
            failed += MousePressNeverArmsHold();
            failed += MouseHoverReveals();
            failed += HoldOutranksHover();
            failed += StaleExitDoesNotCancelLiveHover();
            failed += EveryKeywordHasGlossary();
            failed += ContentUsesLiveStateOverPrint();

            if (failed > 0)
                Debug.LogError($"[CardRevealVerify] {failed} check(s) FAILED.");
            else
                Debug.Log("[CardRevealVerify] GREEN — 8/8 checks passed.");
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
            if (m.Visible) return Fail("TouchLongPressReveals", "still visible after release");

            return Pass("TouchLongPressReveals");
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
            m.Tick(HoverRevealModel.HoverDelaySeconds + HoverRevealModel.HoldSeconds);

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
                id: "verify_minion", name: "Verify Minion", type: CardType.MINION, cost: 1,
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
