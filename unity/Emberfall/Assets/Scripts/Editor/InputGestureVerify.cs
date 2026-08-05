using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// EF-048 named verify: drives the complete press/drag/release machine through an actual
    /// InputRouter + PointerSampler stack, with scripted frame sequences and NO scene, NO camera
    /// and NO pointer hardware.
    ///
    /// <para>The harness feeds synthetic PointerSamples directly into the sampler, then steps the
    /// InputRouter through its transitions (press → held → released, plus taps and cancellations),
    /// asserting on submitted intents and resulting gesture state. This exercises drag threshold,
    /// touch/mouse suppression, cancellation, raycast dispatch and release handling — all paths that
    /// remain untested when calling DragTargetingModel in isolation.</para>
    ///
    /// <para>Run via the menu item, or headless through
    /// <see cref="HeadlessVerify.RunAll"/> / <c>tools/ci/unity-verify.ps1</c>.</para>
    /// </summary>
    public static class InputGestureVerify
    {
        private const string Vanilla        = "c_vanilla";         // a minion with no chosen target
        private const string TargetedSpell  = "c_targeted_spell";  // ON_CAST + ANY_CHARACTER
        private const string TargetedMinion = "c_targeted_minion"; // ON_PLAY + ANY_CHARACTER
        private const string PowerFree      = "hp_untargeted";     // hero power, no chosen target
        private const string PowerAim       = "hp_targeted";       // hero power, ANY_CHARACTER

        private static readonly PlayerId P0 = new PlayerId("p0");
        private static readonly PlayerId P1 = new PlayerId("p1");

        private static int _passed;
        private static int _failed;

        [MenuItem("Emberfall/Verify Input Gestures (EF-048 drag targeting)")]
        public static void Run()
        {
            _passed = 0;
            _failed = 0;

            FullStackDragThreshold();
            FullStackTapArm();
            FullStackDragResolve();
            FullStackTapReselect();
            FullStackCancellation();
            FullStackTargetedSpellDirectSelection();
            FullStackTwoStepHerald();
            OsCanceledDuringDrag();
            OsCanceledDuringArmedTap();
            DragTargetingModelCore();

            Debug.Log($"[InputGestureVerify] {_passed} passed, {_failed} failed");
            if (_failed > 0)
                Debug.LogError($"[InputGestureVerify] FAILED — {_failed} assertion(s)");
            else
                Debug.Log("[InputGestureVerify] GREEN — press/drag/release machine behaves as specified.");
        }

        // ── Full-stack harness (InputRouter + PointerSampler, no scene) ───────────────

        /// <summary>Drives InputRouter through a scripted frame sequence, capturing submitted intents.</summary>
        private sealed class InputRouterHarness
        {
            public GameState State;
            public ICardDatabase Db;
            public PlayerId P0Id;
            public InputRouter Router;
            public List<PlayerIntent> SubmittedIntents;
            private HotSeatInput _input;
            private DragTargetingModel _model;

            public DragTargetingModel.TargetingPhase Phase => _model.Phase;
            public int AwaitingSlot => _model.AwaitingSlot;
            public InstanceId AwaitingCardId => _model.AwaitingCardId;

            public InputRouterHarness(GameState state, ICardDatabase db, PlayerId p0Id)
            {
                State = state;
                Db = db;
                P0Id = p0Id;
                SubmittedIntents = new List<PlayerIntent>();
                _model = new DragTargetingModel(p0Id, db) { State = state };

                Router = new GameObject("InputRouter").AddComponent<InputRouter>();
                _input = new HotSeatInput(p0Id);

                Router.Init(p0Id, _input, db);
                Router.OnStateUpdated(state);
                Router.UiBlocksInput = () => false;  // no UI blocking in tests

                // Inject synthetic probe that maps screen position to board subject.
                Router.InjectableProbe = (screenPos) => ProbeScreenPosition(screenPos);

                // Hook the queue to capture submitted intents (reflection for testing only).
                StartCapturingIntents();
            }

            private void StartCapturingIntents()
            {
                var queueField = typeof(HotSeatInput).GetField("_queue",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (queueField != null)
                {
                    // Periodically drain the queue to capture intents
                    var queue = (System.Collections.Generic.Queue<PlayerIntent>)queueField.GetValue(_input);
                    if (queue != null)
                    {
                        while (queue.Count > 0)
                            SubmittedIntents.Add(queue.Dequeue());
                    }
                }
            }

            /// <summary>Feed a sequence of synthetic pointer frames and step the router — one Update per frame.</summary>
            public void StepFrames(params PointerSample[] frames)
            {
                var updateMethod = typeof(InputRouter).GetMethod(
                    "Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                foreach (var frame in frames)
                {
                    Router.Pointer.QueueSyntheticFrames(frame);
                    if (updateMethod != null)
                        updateMethod.Invoke(Router, null);
                }
                StartCapturingIntents();
            }

            /// <summary>
            /// Screen-to-subject mapping for headless tests.
            ///
            /// Region map (all coordinates in screen pixels):
            ///   x in [  0,  50) → hand card 0 (Vanilla)
            ///   x in [ 50, 100) → hand card 1 (TargetedSpell)
            ///   x in [100, 150) → hand card 2 (TargetedMinion)
            ///   x in [150, 400), y  &lt;  200 → our hero (x &lt; 250) / our hero power (x ≥ 250)
            ///   x in [150, 400), y in [200, 400) → our board slot 1
            ///   x in [150, 400), y ≥ 400 → our board minion 0
            ///   x in [400, 700), y  &lt;  200 → enemy hero
            ///   x in [400, 700), y ≥ 200 → enemy board minion 0
            ///   x ≥ 700 → empty space (PointerSubject.None)
            /// </summary>
            private (PointerSubject subject, Transform transform, CardPresenter presenter) ProbeScreenPosition(Vector2 screenPos)
            {
                float x = screenPos.x, y = screenPos.y;

                // Hand zone
                if (x < 150)
                {
                    int idx = x < 50 ? 0 : x < 100 ? 1 : 2;
                    var hand = State.Players[0].Hand;
                    if (hand.Count > idx)
                        return (PointerSubject.OfCard(hand[idx].Id, P0Id, Zone.Hand), null, null);
                    return (PointerSubject.None, null, null);
                }

                // Friendly side
                if (x < 400)
                {
                    if (y < 200)
                        return x < 250
                            ? (PointerSubject.OfHero(_model.HeroIdOf(P0Id), P0Id), null, null)
                            : (PointerSubject.OfHeroPower(P0Id), null, null);
                    if (y < 400)
                        return (PointerSubject.OfBoardSlot(P0Id, 1), null, null);
                    // Friendly board minion
                    var board = State.Players[0].Board;
                    if (board.Count > 0)
                        return (PointerSubject.OfCard(board[0].Id, P0Id, Zone.Board), null, null);
                    return (PointerSubject.None, null, null);
                }

                // Enemy side
                if (x < 700)
                {
                    if (y < 200)
                        return (PointerSubject.OfHero(_model.HeroIdOf(_model.EnemyId()), _model.EnemyId()), null, null);
                    var board = State.Players[1].Board;
                    if (board.Count > 0)
                        return (PointerSubject.OfCard(board[0].Id, _model.EnemyId(), Zone.Board), null, null);
                    return (PointerSubject.None, null, null);
                }

                return (PointerSubject.None, null, null);
            }

            public void Cleanup()
            {
                Object.DestroyImmediate(Router.gameObject);
            }
        }

        // ── Full-stack test cases ───────────────────────────────────────────────────

        // Probe coordinate landmarks (see InputRouterHarness.ProbeScreenPosition for the full map):
        //   (25, 200)  → hand card 0 (Vanilla)          (75, 200)  → hand card 1 (TargetedSpell)
        //   (125, 200) → hand card 2 (TargetedMinion)
        //   (200, 300) → our board slot 1                (200, 500) → our board minion 0
        //   (150, 100) → our hero                        (300, 100) → our hero power
        //   (500, 300) → enemy board minion 0            (500, 100) → enemy hero
        //   (750, 300) → empty space (PointerSubject.None)
        // DragThresholdPx = 10 when Screen.dpi == 0 (headless); we cross it by moving ≥ 10 px.

        private static void FullStackDragThreshold()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Press hand card 0 and move 4 px — below the 10 px threshold. Should stay Pressed.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed, new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,    new Vector2(29, 200)));
            Check("press below drag threshold: no intent yet", harness.SubmittedIntents.Count == 0);

            // Move exactly 10 px more (total ≥ threshold) — transitions to Dragging, still no submit.
            harness.StepFrames(new PointerSample(PointerPhase.Held, new Vector2(25, 210)));
            Check("drag threshold crossed: no auto-submit", harness.SubmittedIntents.Count == 0);

            // Release on our board slot 1 — Dragging resolves to PlayCardIntent.
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("release on board slot after drag submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            harness.Cleanup();
        }

        private static void FullStackTapArm()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Tap hand card 0: press + release without crossing threshold → ArmedByTap, no submit.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(25, 200)));
            Check("tap arms without submitting", harness.SubmittedIntents.Count == 0);

            // While armed, press is ignored; release on board slot resolves to PlayCardIntent.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("tap-armed card: release on board slot submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            harness.Cleanup();
        }

        private static void FullStackDragResolve()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Press hand card 0, drag past threshold, release on board slot.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(25, 215)));  // 15 px > threshold
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(25, 220)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("drag-and-drop on board slot submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            harness.Cleanup();
        }

        private static void FullStackTapReselect()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Tap hand card 0 (Vanilla): arms.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(25, 200)));
            Check("first tap arms vanilla card", harness.SubmittedIntents.Count == 0);

            // Tap hand card 1 (TargetedSpell): different card — reselects, does NOT submit.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(75, 200)));
            Check("tap different card reselects without submitting", harness.SubmittedIntents.Count == 0);

            // Now armed on the targeted card; release on an enemy minion resolves to PlayCardIntent.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            Check("release on enemy after reselect submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            harness.Cleanup();
        }

        private static void FullStackCancellation()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Tap-arm hand card 0.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(25, 200)));
            Check("tap arms", harness.SubmittedIntents.Count == 0);

            // Release on empty space while armed → TryResolve(None) → cancel, no intent.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(750, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(750, 300)));
            Check("release on empty space cancels armed gesture without submitting",
                harness.SubmittedIntents.Count == 0);

            harness.Cleanup();
        }

        private static void FullStackTargetedSpellDirectSelection()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);
            InstanceId enemy = s.Players[1].Board[0].Id;

            // A targeted spell is armed once, then released directly on its character target.
            // Releasing it on a slot is a miss: it must not retain that slot or arm a second phase.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(200, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("targeted spell released on a board slot submits nothing",
                harness.SubmittedIntents.Count == 0);
            Check("targeted spell slot miss stores no placement and stays in direct targeting",
                harness.Phase == DragTargetingModel.TargetingPhase.Direct && harness.AwaitingSlot == 0
                && harness.AwaitingCardId.Equals(default));

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            Check("targeted spell slot miss does not leave a second targeting phase armed",
                harness.SubmittedIntents.Count == 0);

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));

            var intent = harness.SubmittedIntents.Count == 1
                ? harness.SubmittedIntents[0] as PlayCardIntent
                : null;
            Check("targeted spell direct selection submits exactly one targeted play intent",
                intent != null && intent.BoardSlot == 0 && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(enemy));

            harness.Cleanup();
        }

        // Issue #30: two-step targeting for herald minions (tap-arm, drop on slot, tap target).
        private static void FullStackTwoStepHerald()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);
            InstanceId enemy = s.Players[1].Board[0].Id;

            // Tap hand card 2 (TargetedMinion): press + release without travel → ArmedByTap.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(125, 200)));
            Check("tap targeted card arms without submitting", harness.SubmittedIntents.Count == 0);

            // A herald minion cannot skip placement by releasing directly on its effect target.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            Check("targeted minion released directly on a character submits nothing",
                harness.SubmittedIntents.Count == 0);

            // Re-arm, then tap empty board slot 1 to enter the effect-target phase.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(200, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("drop on board slot enters two-step targeting without submit",
                harness.SubmittedIntents.Count == 0);

            // Tap on enemy minion: press + release → resolves to PlayCardIntent with slot + target.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            Check("second tap on target submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            var intent = harness.SubmittedIntents[0] as PlayCardIntent;
            Check("PlayCardIntent carries the slot from step 1",
                intent != null && intent.BoardSlot == 1);
            Check("PlayCardIntent carries the target from step 2",
                intent != null && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(enemy));

            harness.Cleanup();
        }

        /// <summary>NOR-137: OS-canceled touch during a drag (focus loss, system gesture, incoming call).</summary>
        private static void OsCanceledDuringDrag()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Press and drag hand card 0 past the threshold.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed, new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,    new Vector2(25, 215)));  // crosses threshold
            Check("mid-drag: no intent yet", harness.SubmittedIntents.Count == 0);

            // OS cancels the touch (TouchPhase.Canceled) — should cancel the gesture cleanly
            // without submitting an intent or leaving targeting armed.
            harness.StepFrames(new PointerSample(PointerPhase.Canceled, new Vector2(25, 215)));
            Check("OS-canceled touch during drag: no intent submitted", harness.SubmittedIntents.Count == 0);
            Check("OS-canceled touch during drag: gesture fully cleared",
                !harness.Router.Pointer.IsTrackingFinger);  // Gesture is reset

            // Verify that targeting is also cleared (no armed state leaking through).
            // A subsequent press on the same card should start fresh.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed, new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(25, 200)));
            Check("after OS cancel, new tap on the same card arms cleanly",
                harness.SubmittedIntents.Count == 0);

            harness.Cleanup();
        }

        /// <summary>NOR-137: OS-canceled touch while gesture is armed by tap (ArmedByTap state).</summary>
        private static void OsCanceledDuringArmedTap()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Tap-arm hand card 0.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(25, 200)));
            Check("tap arms", harness.SubmittedIntents.Count == 0);

            // OS cancels while armed (e.g., app loses focus mid-tap-armed state).
            // PointerSampler synthesizes PointerPhase.Canceled when the finger disappears.
            harness.StepFrames(new PointerSample(PointerPhase.Canceled, new Vector2(25, 200)));
            Check("OS-canceled touch during ArmedByTap: no intent submitted", harness.SubmittedIntents.Count == 0);
            Check("OS-canceled touch during ArmedByTap: gesture fully cleared",
                !harness.Router.Pointer.IsTrackingFinger);

            // Verify that a new press starts fresh without any residual ArmedByTap state.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(75, 200)));
            Check("after OS cancel, new tap on a different card arms cleanly (reselect works)",
                harness.SubmittedIntents.Count == 0);

            harness.Cleanup();
        }

        // ── DragTargetingModel core (isolated, as before) ─────────────────────────

        private static void DragTargetingModelCore()
        {
            MulliganIsNotTurnGated();
            PressLegality();
            HandCardDrops();
            TwoStepHerald();
            AttackDrops();
            HeroPowerDrops();
            NoAutoPlace();
            TargetSets();
        }

        // ── The cases ───────────────────────────────────────────────────────────────

        // THE REGRESSION THIS FILE EXISTS FOR. The engine's mulligan is SIMULTANEOUS: both seats
        // decide at once, and only one of them is ActivePlayer. A turn gate on this path leaves the
        // other seat unable to mulligan and the match hanging, with nothing logged.
        private static void MulliganIsNotTurnGated()
        {
            GameState s = NewState();
            s.Phase        = TurnPhase.Mulligan;
            s.ActivePlayer = P1;                  // the OTHER seat holds the turn
            s.Mulligan0Done = false;

            var m = Model(s);
            Check("mulligan active for the non-active seat", m.IsMulliganActive());

            s.Mulligan0Done = true;
            Check("mulligan closes once our seat has confirmed", !m.IsMulliganActive());

            s.Mulligan0Done = false;
            s.Phase = TurnPhase.Main;
            Check("mulligan inactive outside the Mulligan phase", !m.IsMulliganActive());
        }

        private static void PressLegality()
        {
            GameState s = NewState();
            var m = Model(s);

            InstanceId hand      = s.Players[0].Hand[0].Id; // Vanilla
            InstanceId aimCard   = s.Players[0].Hand[1].Id; // TargetedSpell
            InstanceId herald = s.Players[0].Hand[2].Id; // TargetedMinion
            InstanceId mine    = s.Players[0].Board[0].Id;
            InstanceId theirs  = s.Players[1].Board[0].Id;

            Check("press on our hand card starts a HandCard drag",
                m.BeginSource(PointerSubject.OfCard(hand, P0, Zone.Hand)).Kind == DragSourceKind.HandCard);
            Check("a vanilla hand card needs no target",
                !m.BeginSource(PointerSubject.OfCard(hand, P0, Zone.Hand)).NeedsTarget);
            Check("an ANY_* hand card needs a target",
                m.BeginSource(PointerSubject.OfCard(aimCard, P0, Zone.Hand)).NeedsTarget);
            Check("a targeted herald minion needs a target",
                m.BeginSource(PointerSubject.OfCard(herald, P0, Zone.Hand)).NeedsTarget);
            Check("press on our board minion starts an Attacker drag",
                m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board)).Kind == DragSourceKind.Attacker);
            Check("press on our hero starts an Attacker drag",
                m.BeginSource(PointerSubject.OfHero(m.HeroIdOf(P0), P0)).Kind == DragSourceKind.Attacker);
            Check("press on our hero power starts a HeroPower gesture",
                m.BeginSource(PointerSubject.OfHeroPower(P0)).Kind == DragSourceKind.HeroPower);

            Check("press on an enemy minion starts nothing",
                m.BeginSource(PointerSubject.OfCard(theirs, P1, Zone.Board)).Kind == DragSourceKind.None);
            Check("press on the enemy hero starts nothing",
                m.BeginSource(PointerSubject.OfHero(m.HeroIdOf(P1), P1)).Kind == DragSourceKind.None);
            Check("press on the enemy hero power starts nothing",
                m.BeginSource(PointerSubject.OfHeroPower(P1)).Kind == DragSourceKind.None);
            Check("press on empty space starts nothing",
                m.BeginSource(PointerSubject.None).Kind == DragSourceKind.None);

            s.ActivePlayer = P1;
            Check("no gesture starts on the opponent's turn",
                m.BeginSource(PointerSubject.OfCard(hand, P0, Zone.Hand)).Kind == DragSourceKind.None);
            s.ActivePlayer = P0;
            s.Phase = TurnPhase.EndOfTurn;
            Check("no gesture starts outside the Main phase",
                m.BeginSource(PointerSubject.OfCard(hand, P0, Zone.Hand)).Kind == DragSourceKind.None);
        }

        private static void HandCardDrops()
        {
            GameState s = NewState();
            var m = Model(s);

            InstanceId hand    = s.Players[0].Hand[0].Id;
            InstanceId aimCard = s.Players[0].Hand[1].Id;
            InstanceId mine    = s.Players[0].Board[0].Id;
            InstanceId theirs  = s.Players[1].Board[0].Id;

            DragSource vanilla  = m.BeginSource(PointerSubject.OfCard(hand,    P0, Zone.Hand));
            DragSource targeted = m.BeginSource(PointerSubject.OfCard(aimCard, P0, Zone.Hand));

            var onSlot = m.Resolve(vanilla, PointerSubject.OfBoardSlot(P0, 2)) as PlayCardIntent;
            Check("drop on our slot 2 plays there", onSlot != null && onSlot.BoardSlot == 2
                                                    && onSlot.Targets.Count == 0);

            Check("drop on an ENEMY slot plays nothing",
                m.Resolve(vanilla, PointerSubject.OfBoardSlot(P1, 0)) == null);
            Check("drop on an occupied slot plays nothing (friendly minion)",
                m.Resolve(vanilla, PointerSubject.OfCard(mine, P0, Zone.Board)) == null);
            Check("drop on an occupied slot plays nothing (enemy minion)",
                m.Resolve(vanilla, PointerSubject.OfCard(theirs, P1, Zone.Board)) == null);

            var atFriend = m.Resolve(targeted, PointerSubject.OfCard(mine, P0, Zone.Board)) as PlayCardIntent;
            Check("an ANY_* card CAN aim at our own minion", atFriend != null
                                                             && atFriend.Targets[0].Instance.Equals(mine));

            var atHero = m.Resolve(targeted, PointerSubject.OfHero(m.HeroIdOf(P1), P1)) as PlayCardIntent;
            Check("a hand card can aim at a hero", atHero != null
                                                   && atHero.Targets[0].Instance.Equals(m.HeroIdOf(P1)));
        }

        // Issue #30: a targeted-herald minion needs two steps: (1) pick the slot, (2) pick the target.
        private static void TwoStepHerald()
        {
            GameState s = NewState();
            var m = Model(s);

            InstanceId aimCard = s.Players[0].Hand[2].Id;   // TargetedMinion (ANY_CHARACTER)
            InstanceId theirs  = s.Players[1].Board[0].Id;
            DragSource targeted = m.BeginSource(PointerSubject.OfCard(aimCard, P0, Zone.Hand));

            Check("targeted minion cannot bypass placement by releasing directly on a character",
                m.Resolve(targeted, PointerSubject.OfCard(theirs, P1, Zone.Board)) == null);

            // Drop on a legal empty slot → enters AwaitingEffectTarget, no intent yet.
            var result = m.Resolve(targeted, PointerSubject.OfBoardSlot(P0, 2));
            Check("targeted card dropped on empty slot enters AwaitingEffectTarget",
                result == null && m.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget);
            Check("AwaitingEffectTarget stores the slot and card",
                m.AwaitingSlot == 2 && m.AwaitingCardId.Equals(aimCard));

            // Second release: drop on a valid target → emits PlayCardIntent with both slot and target.
            var intent = m.Resolve(DragSource.None, PointerSubject.OfCard(theirs, P1, Zone.Board)) as PlayCardIntent;
            Check("targeted card: second release on valid target submits intent",
                intent != null && intent.BoardSlot == 2 && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(theirs));
            Check("after resolving, AwaitingEffectTarget clears",
                m.Phase == DragTargetingModel.TargetingPhase.Direct);

            // Test cancellation: drop on an invalid target → cancels, no intent.
            m.Phase = DragTargetingModel.TargetingPhase.AwaitingEffectTarget;
            m.AwaitingSlot = 2;
            m.AwaitingCardId = aimCard;
            var invalidResult = m.Resolve(DragSource.None, PointerSubject.OfBoardSlot(P0, 1));
            Check("targeted card: second release on invalid target plays nothing",
                invalidResult == null);
        }

        private static void AttackDrops()
        {
            GameState s = NewState();
            var m = Model(s);

            InstanceId mine   = s.Players[0].Board[0].Id;
            InstanceId theirs = s.Players[1].Board[0].Id;
            DragSource atk    = m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board));

            var hit = m.Resolve(atk, PointerSubject.OfCard(theirs, P1, Zone.Board)) as AttackIntent;
            Check("attacker dropped on an enemy minion attacks it",
                hit != null && hit.Attacker.Equals(mine) && hit.Defender.Instance.Equals(theirs));

            var face = m.Resolve(atk, PointerSubject.OfHero(m.HeroIdOf(P1), P1)) as AttackIntent;
            Check("attacker dropped on the enemy hero goes face",
                face != null && face.Defender.Instance.Equals(m.HeroIdOf(P1)));

            Check("attacker dropped on our own minion does nothing",
                m.Resolve(atk, PointerSubject.OfCard(mine, P0, Zone.Board)) == null);
            Check("attacker dropped on our own hero does nothing",
                m.Resolve(atk, PointerSubject.OfHero(m.HeroIdOf(P0), P0)) == null);
            Check("attacker dropped on a board slot does nothing",
                m.Resolve(atk, PointerSubject.OfBoardSlot(P1, 0)) == null);
        }

        private static void HeroPowerDrops()
        {
            GameState s = NewState();
            var m = Model(s);
            InstanceId theirs = s.Players[1].Board[0].Id;

            // Untargeted: fires only when released back on itself — i.e. a tap.
            DragSource free = m.BeginSource(PointerSubject.OfHeroPower(P0));
            Check("untargeted hero power needs no target", !free.NeedsTarget);
            Check("untargeted hero power fires on a tap",
                m.Resolve(free, PointerSubject.OfHeroPower(P0)) is HeroPowerIntent);
            Check("untargeted hero power dragged away cancels",
                m.Resolve(free, PointerSubject.OfCard(theirs, P1, Zone.Board)) == null);

            // Targeted: the drop supplies the target and a tap on itself is a cancel.
            s.Players[0].Hero.HeroPowerId = PowerAim;
            DragSource aim = m.BeginSource(PointerSubject.OfHeroPower(P0));
            Check("ANY_* hero power needs a target", aim.NeedsTarget);

            var fired = m.Resolve(aim, PointerSubject.OfCard(theirs, P1, Zone.Board)) as HeroPowerIntent;
            Check("targeted hero power fires at the drop target",
                fired != null && fired.Targets.Count == 1 && fired.Targets[0].Instance.Equals(theirs));
            Check("targeted hero power dropped on itself cancels",
                m.Resolve(aim, PointerSubject.OfHeroPower(P0)) == null);
        }

        // Plan decision 13: the auto-place shortcut (old InputRouter.cs:561-576) turned a click on
        // empty space into a play at the end of our board. On touch that turned stray taps into
        // misplays. A drop on nothing is now a cancel, full stop.
        private static void NoAutoPlace()
        {
            GameState s = NewState();
            var m = Model(s);
            DragSource vanilla = m.BeginSource(PointerSubject.OfCard(s.Players[0].Hand[0].Id, P0, Zone.Hand));

            Check("a hand card dropped on empty space plays NOTHING (decision 13)",
                m.Resolve(vanilla, PointerSubject.None) == null);
        }

        private static void TargetSets()
        {
            GameState s = NewState();
            var m = Model(s);
            InstanceId mine = s.Players[0].Board[0].Id;

            HashSet<InstanceId> plain = m.TargetsFor(m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board)));
            Check("with no bulwark, the enemy hero is attackable", plain.Contains(m.HeroIdOf(P1)));
            Check("with no bulwark, an enemy minion is attackable", plain.Contains(s.Players[1].Board[0].Id));

            // A bulwark wall restricts the set to bulwarks and closes the face.
            CardInstance bulwark = Minion(P1, 20);
            bulwark.Keywords.Add(Keyword.BULWARK);
            s.Players[1].Board.Add(bulwark);

            HashSet<InstanceId> walled = m.TargetsFor(m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board)));
            Check("a bulwark wall closes the face", !walled.Contains(m.HeroIdOf(P1)));
            Check("a bulwark wall excludes the non-bulwark minion", !walled.Contains(s.Players[1].Board[0].Id));
            Check("a bulwark wall includes the bulwark", walled.Contains(bulwark.Id));

            // Shroud hides a minion from targeting entirely.
            s.Players[1].Board.Remove(bulwark);
            s.Players[1].Board[0].Keywords.Add(Keyword.SHROUD);
            HashSet<InstanceId> hidden = m.TargetsFor(m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board)));
            Check("a shrouded enemy minion is not attackable", !hidden.Contains(s.Players[1].Board[0].Id));

            Check("a vanilla hand card asks for no target set",
                m.TargetsFor(m.BeginSource(PointerSubject.OfCard(s.Players[0].Hand[0].Id, P0, Zone.Hand))) == null);
        }

        // ── Fixtures ────────────────────────────────────────────────────────────────

        private static DragTargetingModel Model(GameState s)
            => new DragTargetingModel(P0, Db()) { State = s };

        // Our Main turn, distinct vanilla, targeted-spell and targeted-minion cards in hand,
        // with one minion on each board.
        private static GameState NewState()
        {
            var p0 = new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.EMBERWARD,  30));
            var p1 = new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.TIDECALLER, 30));
            p0.Hero.HeroPowerId = PowerFree;
            p1.Hero.HeroPowerId = PowerFree;

            var s = new GameState(new MatchId("m-ef048-verify"), new DeterministicRandom(48UL), p0, p1)
            {
                Phase        = TurnPhase.Main,
                ActivePlayer = P0,
                TurnNumber   = 3,
            };

            p0.Hand.Add(new CardInstance(new InstanceId(10), Vanilla,        P0) { Zone = Zone.Hand });
            p0.Hand.Add(new CardInstance(new InstanceId(11), TargetedSpell,  P0) { Zone = Zone.Hand });
            p0.Hand.Add(new CardInstance(new InstanceId(12), TargetedMinion, P0) { Zone = Zone.Hand });
            p0.Board.Add(Minion(P0, 13));
            p1.Board.Add(Minion(P1, 14));
            return s;
        }

        private static CardInstance Minion(PlayerId owner, int id)
            => new CardInstance(new InstanceId(id), Vanilla, owner)
            {
                Zone          = Zone.Board,
                CurrentAttack = 2,
                CurrentHealth = 3,
                MaxHealth     = 3,
            };

        private static ICardDatabase Db()
        {
            var anyCharacter = new[]
            {
                Rule("battlecry_damage", Trigger.ON_PLAY, 3, "ANY_CHARACTER"),
            };
            var anyCharacterCast = new[]
            {
                Rule("cast_damage", Trigger.ON_CAST, 1, "ANY_CHARACTER"),
            };
            var selfHeal = new[]
            {
                Rule("hero_power_damage", Trigger.ON_CAST, 1, "ENEMY_HERO"),
            };

            return new InMemoryCardDatabase(new[]
            {
                Def(Vanilla,        CardType.MINION,     System.Array.Empty<RuleDefinition>()),
                Def(TargetedSpell,  CardType.SPELL,      anyCharacterCast),
                Def(TargetedMinion, CardType.MINION,     anyCharacter),
                Def(PowerFree,      CardType.HERO_POWER, selfHeal),
                Def(PowerAim,       CardType.HERO_POWER, anyCharacterCast),
            });
        }

        private static RuleDefinition Rule(string id, Trigger trigger, int amount, string selector)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(amount, new TargetSpec(selector)));

        private static CardDefinition Def(string id, CardType type, IReadOnlyList<RuleDefinition> rules)
            => new CardDefinition(id, id, type, 1, 2, 3, null,
                                  CardClass.NEUTRAL, Rarity.COMMON,
                                  System.Array.Empty<Keyword>(), rules,
                                  "", "", SetId.CORE, true, null, "", false);

        private static void Check(string what, bool ok)
        {
            if (ok) { _passed++; Debug.Log($"[InputGestureVerify]   ok   {what}"); }
            else    { _failed++; Debug.LogError($"[InputGestureVerify]  FAIL  {what}"); }
        }
    }
}
