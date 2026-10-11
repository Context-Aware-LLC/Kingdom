using System;
using System.Collections.Generic;
using System.Reflection;
using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Exercises the real event subscriber after a played card has been hidden.</summary>
    public static class CardMaterialBinderVerify
    {
        public static void Run()
        {
            foreach (var kind in new[] { GameEventKind.HealthRestored, GameEventKind.DamageApplied })
            {
                CheckHiddenCard(kind, false, false);
                CheckHiddenCard(kind, true, false);
                CheckHiddenCard(kind, false, true);
            }

            CheckBoardStatusEffectGate();
        }

        private static void CheckBoardStatusEffectGate()
        {
            var parent = new GameObject("CardMaterialBinderBoardStatusVerify");
            try
            {
                var card = new GameObject("Card");
                card.transform.SetParent(parent.transform);
                var shields = card.AddComponent<SpriteRenderer>();
                var left = new GameObject("Left").AddComponent<SpriteRenderer>();
                var right = new GameObject("Right").AddComponent<SpriteRenderer>();
                var lower = new GameObject("Lower").AddComponent<SpriteRenderer>();
                left.transform.SetParent(card.transform);
                right.transform.SetParent(card.transform);
                lower.transform.SetParent(card.transform);
                var smoke = card.AddComponent<ShroudSmokeView>();
                smoke.Setup(left, right, lower);
                var frostLeft = new GameObject("FrostLeft").AddComponent<SpriteRenderer>();
                var frostRight = new GameObject("FrostRight").AddComponent<SpriteRenderer>();
                var frostLower = new GameObject("FrostLower").AddComponent<SpriteRenderer>();
                frostLeft.transform.SetParent(card.transform);
                frostRight.transform.SetParent(card.transform);
                frostLower.transform.SetParent(card.transform);
                var frostCrystals = card.AddComponent<FrostCrystalView>();
                frostCrystals.Setup(frostLeft, frostRight, frostLower);
                var binder = card.AddComponent<CardMaterialBinder>();
                var live = new StubLiveState(new InstanceId(321),
                    AuraSet.Bulwark | AuraSet.Shroud | AuraSet.Frosted);

                binder.SetupRenderers(null, null, null, null, shields, smoke, frostCrystals);
                binder.Init(live, null);
                if (shields.enabled || left.enabled || right.enabled || lower.enabled
                    || frostLeft.enabled || frostRight.enabled || frostLower.enabled)
                    throw new InvalidOperationException("Board status effects appeared while the card was in hand.");

                binder.SetBoardStatusEffectsVisible(true);
                if (!shields.enabled || !left.enabled || !right.enabled || !lower.enabled
                    || !frostLeft.enabled || !frostRight.enabled || !frostLower.enabled)
                    throw new InvalidOperationException(
                        "Bulwark, Shroud, or Frost treatment did not appear on a board minion.");

                binder.SetBoardStatusEffectsVisible(false);
                if (shields.enabled || left.enabled || right.enabled || lower.enabled
                    || frostLeft.enabled || frostRight.enabled || frostLower.enabled)
                    throw new InvalidOperationException("A board status effect remained visible after the card left the board.");

                Debug.Log("[CardMaterialBinderVerify] checked board-only Bulwark, Shroud, and Frost gates");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        private static void CheckHiddenCard(GameEventKind kind, bool cardIsTarget, bool hideParent)
        {
            var parent = new GameObject("CardMaterialBinderVerify");
            try
            {
                var runner = parent.AddComponent<MatchRunner>();
                var card = new GameObject("Card");
                card.transform.SetParent(parent.transform);
                var binder = card.AddComponent<CardMaterialBinder>();
                var id = new InstanceId(123);
                binder.SetupRenderers(null, null, null);
                binder.Init(new CardLiveState(id), runner);

                // BoardController.OnPlayReveal hides the hand card before MatchRunner dispatches
                // the result batch. The binder remains subscribed until end-of-frame destruction.
                (hideParent ? parent : card).SetActive(false);
                var eventField = typeof(MatchRunner).GetField("OnMatchEvent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var dispatch = (Action<IReadOnlyList<GameEvent>>)eventField.GetValue(runner);
                if (dispatch == null)
                    throw new InvalidOperationException("The card binder was not subscribed.");

                dispatch(new[] { new GameEvent(kind, 1,
                    cardIsTarget ? new InstanceId(456) : id,
                    cardIsTarget ? id : new InstanceId(456), 4, EventFlags.None) });

                // HeadlessVerify captures Unity errors as failures, including StartCoroutine's
                // inactive-GameObject error (which does not throw a managed exception).
                Debug.Log($"[CardMaterialBinderVerify] checked {kind}, target={cardIsTarget}, parentHidden={hideParent}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        private sealed class StubLiveState : ICardLiveState
        {
            public StubLiveState(InstanceId id, AuraSet auras)
            {
                Id = id;
                Auras = auras;
            }

            public InstanceId Id { get; }
            public int Attack => 0;
            public int Health => 0;
            public int MaxHealth => 0;
            public IReadOnlyCollection<Keyword> Keywords => Array.Empty<Keyword>();
            public AuraSet Auras { get; }
            public int BoardSlot => 0;
            public AttackReadiness AttackReadiness => Kingdom.Presentation.AttackReadiness.None;
            public int SpellDamageBonus => 0;
            public int? Durability => null;
            public event Action<CardChange> Changed { add { } remove { } }
        }
    }
}
