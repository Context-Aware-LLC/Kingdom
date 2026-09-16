using System;
using System.Collections.Generic;
using System.Reflection;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Editor
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
    }
}
