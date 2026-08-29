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
        private const int Vanilla       = 9001;   // a minion with no chosen target
        private const int TargetedSpell = 9002;   // ON_CAST + ANY_CHARACTER
        private const int TargetedMinion = 9003;   // ON_PLAY + ANY_CHARACTER
        private const int PowerFree     = 41;   // hero power, no chosen target
        private const int PowerAim      = 42;   // hero power, ANY_CHARACTER
        private const int FizzleMinion  = 9004;   // ON_PLAY + ANY_FRIENDLY_MINION
        private const int CostMinion    = 9005;   // same, but target.cost = Required

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
            FullStackTapDoesNotArm();
            FullStackDragResolve();
            FullStackDragCancellation();
            FullStackOpenBoardPlays();
            FullStackTargetedSpellDrag();
            FullStackTargetedMinionDropDoesNotAim();
            FullStackTwoStepHeraldClick();
            FullStackTwoStepHeraldKeyboard();
            OsCanceledDuringDrag();
            OsCanceledDuringAwaitingEffectDrag();
            ProjectionRoundTrips();
            DragTargetingModelCore();

            Debug.Log($"[InputGestureVerify] {_passed} passed, {_failed} failed");
            if (_failed > 0)
                Debug.LogError($"[InputGestureVerify] FAILED — {_failed} assertion(s)");
            else
                Debug.Log("[InputGestureVerify] GREEN — press/drag/release machine behaves as specified.");
        }

        /// <summary>
        /// The pointer-to-board seam must survive the camera migration. A world point projected
        /// to screen and resolved back onto its source plane has to round-trip under both the old
        /// orthographic camera and the approved angled perspective camera.
        /// </summary>
        private static void ProjectionRoundTrips()
        {
            var cameraObject = new GameObject("ProjectionVerifyCamera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.pixelRect = new Rect(0f, 0f, 1600f, 900f);

            try
            {
                camera.orthographic = true;
                camera.orthographicSize = 4.6f;
                camera.transform.SetPositionAndRotation(
                    new Vector3(0f, 0f, -10f), Quaternion.identity);

                CheckProjectionRoundTrip(
                    "orthographic pointer projection round-trips the gameplay plane",
                    camera,
                    new Vector3(-3.25f, 1.4f, 0f));

                camera.orthographic = false;
                camera.fieldOfView = 36f;
                camera.transform.position = new Vector3(0f, -5.15f, -18f);
                camera.transform.LookAt(new Vector3(0f, 0.35f, 0.45f));

                CheckProjectionRoundTrip(
                    "angled perspective projection round-trips the gameplay plane",
                    camera,
                    new Vector3(4.1f, -1.25f, 0f));
                CheckProjectionRoundTrip(
                    "angled perspective projection round-trips the lifted drag plane",
                    camera,
                    new Vector3(-1.8f, 0.7f, -0.65f));

                camera.transform.SetPositionAndRotation(
                    new Vector3(0f, 0f, -10f), Quaternion.LookRotation(Vector3.right));
                Check("a camera ray parallel to the board plane is rejected",
                    !BoardPlaneProjection.TryScreenToPlane(
                        camera, new Vector2(800f, 450f), 0f, out _));
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        private static void CheckProjectionRoundTrip(string what, Camera camera, Vector3 expected)
        {
            Vector3 screen = camera.WorldToScreenPoint(expected);
            bool hit = BoardPlaneProjection.TryScreenToPlane(
                camera, new Vector2(screen.x, screen.y), expected.z, out Vector3 actual);
            Check(what, hit && Vector3.Distance(expected, actual) < 0.001f);
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
            public HashSet<InstanceId> ActiveTargets;
            public InstanceId? FocusedTarget;
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

                Router = new GameObject("InputRouter").AddComponent<InputRouter>();
                _input = new HotSeatInput(p0Id);

                Router.Init(p0Id, _input, db);
                Router.OnStateUpdated(state);
                Router.TargetingChanged += targets => ActiveTargets = targets;
                Router.TargetFocusChanged += target => FocusedTarget = target;

                // THE ROUTER'S OWN MODEL, not a second one. This used to be
                // `new DragTargetingModel(...)` — a parallel instance the router never touched, so
                // Phase and AwaitingSlot below reported a model that no gesture ever ran through.
                // Every assertion on them was vacuously true, which is why they all "passed" while
                // asserting the opposite of what the router did. Found 2026-08-12 when #139 added
                // the first POSITIVE assertion on Phase and it failed against a live router.
                var modelField = typeof(InputRouter).GetField("_model",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                _model = (DragTargetingModel)modelField.GetValue(Router);
                Router.UiBlocksInput = () => false;  // no UI blocking in tests

                // Inject synthetic probe that maps screen position to board subject.
                Router.InjectableProbe = (screenPos) => ProbeScreenPosition(screenPos);

                // ...and the drop point, which since #139 decides as much as the probe does. There
                // is no Camera.main here, so without this every release would report world x 0.
                Router.InjectableDrop = (screenPos) => DropForScreenPosition(screenPos);

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

            public bool CycleTarget(bool reverse = false) => Router.CycleAwaitingTarget(reverse);

            public bool ConfirmTarget()
            {
                bool result = Router.ConfirmAwaitingTarget();
                StartCapturingIntents();
                return result;
            }

            /// <summary>
            /// Screen-to-subject mapping for headless tests.
            ///
            /// Region map (all coordinates in screen pixels):
            ///   x in [  0,  50) → hand card 0 (Vanilla)
            ///   x in [ 50, 100) → hand card 1 (TargetedSpell)
            ///   x in [100, 150) → hand card 2 (TargetedMinion)
            ///   x in [150, 400), y  &lt;  200 → our hero (x &lt; 250) / our hero power (x ≥ 250)
            ///   x in [150, 400), y in [200, 400) → empty board space (PointerSubject.None)
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
                    // Empty board space. It used to answer OfBoardSlot(P0Id, 1); since #139 nothing
                    // is raycast there at all, and where a minion lands comes from the drop's x.
                    if (y < 400)
                        return (PointerSubject.None, null, null);
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

            /// <summary>
            /// Screen-to-drop-point mapping for headless tests — the other half of a release since
            /// #139. Deliberately independent of the probe map above, because that is the real
            /// relationship: what the pointer is OVER and WHERE it is are separate facts now.
            ///
            ///   x &lt; 150          → still in the hand — the probe's hand zone, kept in step with it
            ///   y &lt; 100          → still in the hand, but NOT over a card. The real rule is a
            ///                        line (HandFanLayout.TopEdgeY), not the hand's colliders, and
            ///                        this is the only way to press on that distinction here.
            ///   otherwise        → board, world x = (screenX - 400) / 100
            ///
            /// So x=200 → world -2.0, x=500 → world +1.0, x=750 → world +3.5. Against this suite's
            /// one-minion board (that minion at world x 0) those resolve to insert 0, 1 and 1.
            /// </summary>
            private static DropPoint DropForScreenPosition(Vector2 screenPos)
                => screenPos.x < 150 || screenPos.y < 100
                    ? DropPoint.OwnHand
                    : DropPoint.Board((screenPos.x - 400f) / 100f);

            public void Cleanup()
            {
                Object.DestroyImmediate(Router.gameObject);
            }
        }

        // ── Full-stack test cases ───────────────────────────────────────────────────

        // Probe coordinate landmarks (see InputRouterHarness.ProbeScreenPosition for the full map):
        //   (25, 200)  → hand card 0 (Vanilla)          (75, 200)  → hand card 1 (TargetedSpell)
        //   (125, 200) → hand card 2 (TargetedMinion)
        //   (200, 300) → open board, world x -2.0        (200, 500) → our board minion 0
        //   (750,  50) → still in hand (cancel)         (750, 300) → open board, world x +3.5
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

            // Release on open board — Dragging resolves to PlayCardIntent.
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("release on the board after drag submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            harness.Cleanup();
        }

        private static void FullStackTapDoesNotArm()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // A targeted card tap is a read/no-op. It must neither submit nor leave legal targets
            // glowing: gameplay targeting begins only after pointer travel crosses the threshold.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(75, 200)));
            Check("tap on a targeted card does not submit", harness.SubmittedIntents.Count == 0);
            Check("tap on a targeted card does not arm indicators", harness.ActiveTargets == null);

            // A subsequent tap on a legal target must not complete click-click targeting.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            Check("target tap after source tap still submits nothing", harness.SubmittedIntents.Count == 0);

            harness.Cleanup();
        }

        private static void FullStackDragResolve()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Press hand card 0, drag past threshold, release on the board.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(25, 215)));  // 15 px > threshold
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(25, 220)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("drag-and-drop on the board submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            harness.Cleanup();
        }

        private static void FullStackDragCancellation()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            // Drag a hand card out, then put it back over the hand: cancel, no intent.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(25, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(750, 50)));
            Check("drag released back over the hand cancels without submitting",
                harness.SubmittedIntents.Count == 0);

            harness.Cleanup();
        }

        /// <summary>#139: the same gesture one row higher now PLAYS, where it used to be swallowed.
        /// The pair with FullStackCancellation above is the whole ruling in two tests.</summary>
        private static void FullStackOpenBoardPlays()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(25, 215)));

            // (750, 300) is open space to the probe — nothing under the pointer at all — and world
            // x +3.5 to the drop map, which clamps to the end of our one-minion row.
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(750, 300)));

            var intent = harness.SubmittedIntents.Count == 1
                ? harness.SubmittedIntents[0] as PlayCardIntent
                : null;
            Check("release on open board space submits a play at the x it was released",
                intent != null && intent.BoardSlot == 1);

            harness.Cleanup();
        }

        private static void FullStackTargetedSpellDrag()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);
            InstanceId enemy = s.Players[1].Board[0].Id;

            // A targeted spell must travel from source to target in one drag. Releasing it on open
            // board is a miss: it must not bank a position or arm a second phase.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(75, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("targeted spell released on open board space submits nothing",
                harness.SubmittedIntents.Count == 0);
            Check("targeted spell open-board miss stores no placement and stays in direct targeting",
                harness.Phase == DragTargetingModel.TargetingPhase.Direct && harness.AwaitingSlot == 0
                && harness.AwaitingCardId.Equals(default));

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(75, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(75, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));

            var intent = harness.SubmittedIntents.Count == 1
                ? harness.SubmittedIntents[0] as PlayCardIntent
                : null;
            Check("targeted spell direct selection submits exactly one targeted play intent",
                intent != null && intent.BoardSlot == 0 && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(enemy));

            harness.Cleanup();
        }

        private static void FullStackTargetedMinionDropDoesNotAim()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);
            InstanceId enemy = s.Players[1].Board[0].Id;

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(125, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));

            Check("targeted minion drop chooses placement, not the character underneath",
                harness.SubmittedIntents.Count == 0
                && harness.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget
                && harness.AwaitingSlot == 1);

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            var intent = harness.SubmittedIntents.Count == 1
                ? harness.SubmittedIntents[0] as PlayCardIntent
                : null;
            Check("targeted minion resolves only after the follow-up target click",
                intent != null && intent.BoardSlot == 1 && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(enemy)
                && harness.Phase == DragTargetingModel.TargetingPhase.Direct);

            harness.Cleanup();
        }

        // A targeted minion dropped on open space banks its placement, then accepts a direct click
        // on a legal target. The initial play is still a drag; only the owed target becomes a
        // button-like selection after placement is fixed.
        private static void FullStackTwoStepHeraldClick()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);
            InstanceId enemy = s.Players[1].Board[0].Id;

            // Drag hand card 2 to open board space to bank its placement.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(125, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("drop on open board enters two-step targeting without submit",
                harness.SubmittedIntents.Count == 0);
            Check("...banking the position solved from the drop's x",
                harness.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget
                && harness.AwaitingSlot == 0);

            // An invalid click leaves the banked placement intact.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(750, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(750, 300)));
            Check("empty-space click during the second phase submits nothing",
                harness.SubmittedIntents.Count == 0
                && harness.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget);

            // A legal target click resolves immediately on release.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(500, 300)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(500, 300)));
            Check("legal target click submits PlayCardIntent",
                harness.SubmittedIntents.Count == 1 && harness.SubmittedIntents[0] is PlayCardIntent);

            var intent = harness.SubmittedIntents.Count == 1
                ? harness.SubmittedIntents[0] as PlayCardIntent
                : null;
            Check("PlayCardIntent carries the placement from step 1",
                intent != null && intent.BoardSlot == 0);
            Check("PlayCardIntent carries the target from step 2",
                intent != null && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(enemy));

            harness.Cleanup();
        }

        private static void FullStackTwoStepHeraldKeyboard()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(125, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));

            Check("Tab path starts only after a targeted minion placement is banked",
                harness.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget);
            Check("Tab focuses a legal target", harness.CycleTarget()
                && harness.FocusedTarget.HasValue
                && harness.ActiveTargets.Contains(harness.FocusedTarget.Value));
            Check("Enter/Space confirmation path submits the focused target",
                harness.ConfirmTarget()
                && harness.SubmittedIntents.Count == 1
                && harness.SubmittedIntents[0] is PlayCardIntent);
            Check("keyboard confirmation clears target focus",
                !harness.FocusedTarget.HasValue
                && harness.Phase == DragTargetingModel.TargetingPhase.Direct);

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

            // Verify that targeting is also cleared (no active state leaking through).
            // A subsequent press on the same card should start fresh.
            harness.StepFrames(new PointerSample(PointerPhase.Pressed, new Vector2(25, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(25, 200)));
            Check("after OS cancel, a new tap on the same card remains inert",
                harness.SubmittedIntents.Count == 0);

            harness.Cleanup();
        }

        /// <summary>An OS cancellation while a targeted minion waits for its second drag clears it.</summary>
        private static void OsCanceledDuringAwaitingEffectDrag()
        {
            GameState s = NewState();
            var harness = new InputRouterHarness(s, Db(), P0);

            harness.StepFrames(new PointerSample(PointerPhase.Pressed,  new Vector2(125, 200)));
            harness.StepFrames(new PointerSample(PointerPhase.Held,     new Vector2(125, 215)));
            harness.StepFrames(new PointerSample(PointerPhase.Released, new Vector2(200, 300)));
            Check("targeted minion placement waits for an effect-target drag",
                harness.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget);

            harness.StepFrames(new PointerSample(PointerPhase.Canceled, new Vector2(200, 300)));
            Check("OS cancel during effect-target wait submits nothing", harness.SubmittedIntents.Count == 0);
            Check("OS cancel during effect-target wait clears pointer tracking",
                !harness.Router.Pointer.IsTrackingFinger);

            Check("OS cancel clears the banked effect-target phase",
                harness.Phase == DragTargetingModel.TargetingPhase.Direct);

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
            DropOnOpenBoardPlays();
            TargetSets();
            TargetlessHeraldReturnsToHand();
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

            // #139 (Sam, 2026-08-12). Our board holds exactly one minion, and BoardRowGeometry centres
            // it at world x 0, so it is the only boundary on the row: drop left of it and you are
            // position 0, right of it and you are position 1. Nothing else in the drop is read.
            var toLeft = m.Resolve(vanilla, PointerSubject.None, DropPoint.Board(-1.35f)) as PlayCardIntent;
            Check("a minion dropped LEFT of our only minion takes position 0",
                toLeft != null && toLeft.BoardSlot == 0 && toLeft.Targets.Count == 0);

            var toRight = m.Resolve(vanilla, PointerSubject.None, DropPoint.Board(1.35f)) as PlayCardIntent;
            Check("a minion dropped RIGHT of it takes position 1",
                toRight != null && toRight.BoardSlot == 1 && toRight.Targets.Count == 0);

            var offRow = m.Resolve(vanilla, PointerSubject.None, DropPoint.Board(40f)) as PlayCardIntent;
            Check("an x far past the end of the row clamps to the last position, it does not miss",
                offRow != null && offRow.BoardSlot == 1);

            // THE HEADLINE REVERSAL: the enemy's half of the board used to cancel silently, on two
            // separate paths (an enemy slot, and an enemy minion). Y is irrelevant now, so releasing
            // over the enemy's board plays the minion on OUR row at that x.
            var enemySide = m.Resolve(vanilla, PointerSubject.OfCard(theirs, P1, Zone.Board),
                                      DropPoint.Board(1.35f)) as PlayCardIntent;
            Check("a minion released over the ENEMY board still plays, at the x it was dropped",
                enemySide != null && enemySide.BoardSlot == 1);

            // Dropping ON one of our own minions is no longer a special case at all — it is just a
            // drop whose x happens to be that minion's, and the x rule answers it.
            var onOurMinion = m.Resolve(vanilla, PointerSubject.OfCard(mine, P0, Zone.Board),
                                        DropPoint.Board(0f)) as PlayCardIntent;
            Check("drop on our own minion places at that minion's position", onOurMinion != null
                                                         && onOurMinion.BoardSlot == 0
                                                         && onOurMinion.Targets.Count == 0);

            // The one and only cancel.
            Check("a minion released back over our own hand plays nothing",
                m.Resolve(vanilla, PointerSubject.None, DropPoint.OwnHand) == null);

            var atFriend = m.Resolve(targeted, PointerSubject.OfCard(mine, P0, Zone.Board),
                                     DropPoint.Board(0f)) as PlayCardIntent;
            Check("an ANY_* card CAN aim at our own minion", atFriend != null
                                                             && atFriend.Targets[0].Instance.Equals(mine));

            var atHero = m.Resolve(targeted, PointerSubject.OfHero(m.HeroIdOf(P1), P1),
                                   DropPoint.Board(0f)) as PlayCardIntent;
            Check("a hand card can aim at a hero", atHero != null
                                                   && atHero.Targets[0].Instance.Equals(m.HeroIdOf(P1)));

            // Exception 2: a targeted spell has no position to fall back on, so open space is still
            // nothing. This is the ONLY drop on the board that a card can still fail to resolve on.
            Check("a TARGETED spell released on open board space plays nothing",
                m.Resolve(targeted, PointerSubject.None, DropPoint.Board(1.35f)) == null);
        }

        // A targeted-herald minion always resolves placement first, then its effect target.
        private static void TwoStepHerald()
        {
            GameState s = NewState();
            var m = Model(s);

            InstanceId aimCard = s.Players[0].Hand[2].Id;   // TargetedMinion (ANY_CHARACTER)
            InstanceId theirs  = s.Players[1].Board[0].Id;
            DragSource targeted = m.BeginSource(PointerSubject.OfCard(aimCard, P0, Zone.Hand));

            // Even a legal character underneath the placement drop is not implicitly chosen.
            var onCharacter = m.Resolve(targeted, PointerSubject.OfCard(theirs, P1, Zone.Board),
                                        DropPoint.Board(1.35f));
            Check("targeted minion released on a character still banks placement first",
                onCharacter == null && m.AwaitingSlot == 1
                && m.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget);
            var characterIntent = m.Resolve(DragSource.None,
                PointerSubject.OfCard(theirs, P1, Zone.Board), DropPoint.Board(0f));
            Check("the character becomes the target only on the follow-up selection",
                characterIntent is PlayCardIntent
                && m.Phase == DragTargetingModel.TargetingPhase.Direct);

            // Drop on open board space → same thing, position solved from x.
            var result = m.Resolve(targeted, PointerSubject.None, DropPoint.Board(-1.35f));
            Check("targeted card dropped on open board enters AwaitingEffectTarget",
                result == null && m.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget);
            Check("AwaitingEffectTarget stores the placement and card",
                m.AwaitingSlot == 0 && m.AwaitingCardId.Equals(aimCard));

            // Second release: drop on a valid target → emits PlayCardIntent with both slot and target.
            var intent = m.Resolve(DragSource.None, PointerSubject.OfCard(theirs, P1, Zone.Board),
                                   DropPoint.Board(0f)) as PlayCardIntent;
            Check("targeted card: second release on valid target submits intent",
                intent != null && intent.BoardSlot == 0 && intent.Targets.Count == 1
                && intent.Targets[0].Instance.Equals(theirs));
            Check("after resolving, AwaitingEffectTarget clears",
                m.Phase == DragTargetingModel.TargetingPhase.Direct);

            // Test cancellation: the AIM step still needs a real target — open space is not one, and
            // the drop point is not consulted at all once a placement is banked.
            m.Phase = DragTargetingModel.TargetingPhase.AwaitingEffectTarget;
            m.AwaitingSlot = 1;
            m.AwaitingCardId = aimCard;
            var invalidResult = m.Resolve(DragSource.None, PointerSubject.None, DropPoint.Board(1.35f));
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

            // ATTACKS ARE UNCHANGED BY #139 and still need a named defender: an attack has no
            // position to fall back on, so there is nothing for "anywhere" to resolve to. The drop
            // point is accepted and ignored on every one of these.
            var hit = m.Resolve(atk, PointerSubject.OfCard(theirs, P1, Zone.Board),
                                DropPoint.Board(1.35f)) as AttackIntent;
            Check("attacker dropped on an enemy minion attacks it",
                hit != null && hit.Attacker.Equals(mine) && hit.Defender.Instance.Equals(theirs));

            var face = m.Resolve(atk, PointerSubject.OfHero(m.HeroIdOf(P1), P1),
                                 DropPoint.Board(0f)) as AttackIntent;
            Check("attacker dropped on the enemy hero goes face",
                face != null && face.Defender.Instance.Equals(m.HeroIdOf(P1)));

            Check("attacker dropped on our own minion does nothing",
                m.Resolve(atk, PointerSubject.OfCard(mine, P0, Zone.Board), DropPoint.Board(0f)) == null);
            Check("attacker dropped on our own hero does nothing",
                m.Resolve(atk, PointerSubject.OfHero(m.HeroIdOf(P0), P0), DropPoint.Board(0f)) == null);
            Check("attacker dropped on empty board space does nothing",
                m.Resolve(atk, PointerSubject.None, DropPoint.Board(1.35f)) == null);
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
                m.Resolve(free, PointerSubject.OfHeroPower(P0), DropPoint.Board(0f)) is HeroPowerIntent);
            Check("untargeted hero power dragged away cancels",
                m.Resolve(free, PointerSubject.OfCard(theirs, P1, Zone.Board), DropPoint.Board(0f)) == null);

            // Targeted: the drop supplies the target and a tap on itself is a cancel.
            s.Players[0].Hero.HeroPowerId = PowerAim;
            DragSource aim = m.BeginSource(PointerSubject.OfHeroPower(P0));
            Check("ANY_* hero power needs a target", aim.NeedsTarget);

            var fired = m.Resolve(aim, PointerSubject.OfCard(theirs, P1, Zone.Board),
                                  DropPoint.Board(0f)) as HeroPowerIntent;
            Check("targeted hero power fires at the drop target",
                fired != null && fired.Targets.Count == 1 && fired.Targets[0].Instance.Equals(theirs));
            Check("targeted hero power dropped on itself cancels",
                m.Resolve(aim, PointerSubject.OfHeroPower(P0), DropPoint.Board(0f)) == null);
        }

        // WAS NoAutoPlace, asserting plan decision 13: a drop on nothing is a cancel, full stop.
        // Sam reversed that on 2026-08-12 (#139) — "once that card is dragged out of the hand, if
        // you let go of it anywhere, it should resolve". The concern decision 13 was protecting
        // against (a stray TAP on empty space becoming a play at the end of the board) is answered
        // a different way now: gameplay taps are inert and only a drag crossing the travel
        // threshold can resolve; where it lands is the x you released at rather than "the end".
        private static void DropOnOpenBoardPlays()
        {
            GameState s = NewState();
            var m = Model(s);
            DragSource vanilla = m.BeginSource(PointerSubject.OfCard(s.Players[0].Hand[0].Id, P0, Zone.Hand));

            Check("a hand card released on open board space PLAYS (#139 reverses decision 13)",
                m.Resolve(vanilla, PointerSubject.None, DropPoint.Board(1.35f)) is PlayCardIntent);
            Check("...and the same release back over the hand still does not",
                m.Resolve(vanilla, PointerSubject.None, DropPoint.OwnHand) == null);
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

        /// <summary>
        /// A herald whose target set is empty owes no aim step, but dropping it now returns it to
        /// hand rather than allowing its effect to fizzle into play (Sam, 2026-08-29).
        ///
        /// <para>THE DEAD END THIS GUARDS. NeedsTarget used to read the card DEFINITION, which does
        /// not know the board. A Reinforce herald with no friendly minion armed an aim step holding
        /// zero candidates: nothing glowed and every drop entered a mode that could not complete.
        /// Ask the plan for the live candidate set, then cancel the drop when that set is empty.</para>
        /// </summary>
        private static void TargetlessHeraldReturnsToHand()
        {
            GameState s = NewState();
            s.Players[0].Board.Clear();                 // nothing friendly to reinforce
            s.Players[0].Hand.Add(new CardInstance(new InstanceId(20), FizzleMinion, P0) { Zone = Zone.Hand });
            s.Players[0].Hand.Add(new CardInstance(new InstanceId(21), CostMinion,   P0) { Zone = Zone.Hand });
            var m = Model(s);

            DragSource fizzle = m.BeginSource(PointerSubject.OfCard(new InstanceId(20), P0, Zone.Hand));
            Check("a herald with no legal target needs no aim step", !fizzle.NeedsTarget);
            Check("...and glows nothing", m.TargetsFor(fizzle) == null);

            var played = m.Resolve(fizzle, PointerSubject.None, DropPoint.Board(-1.35f));
            Check("...and returns to hand when dropped with no legal target", played == null);
            Check("...leaving the machine in Direct, ready for another card",
                m.Phase == DragTargetingModel.TargetingPhase.Direct);

            // A COST is not an effect: it stays required, and the engine still refuses the play.
            DragSource cost = m.BeginSource(PointerSubject.OfCard(new InstanceId(21), P0, Zone.Hand));
            Check("a required (cost) target is still owed with nothing to pay it", cost.NeedsTarget);

            // Put a friendly minion back and the ordinary herald owes its target again.
            s.Players[0].Board.Add(Minion(P0, 13));
            DragSource armed = m.BeginSource(PointerSubject.OfCard(new InstanceId(20), P0, Zone.Hand));
            Check("the same herald owes a target once one exists", armed.NeedsTarget);
            Check("...and glows exactly that minion",
                m.TargetsFor(armed) is HashSet<InstanceId> set && set.Count == 1
                    && set.Contains(new InstanceId(13)));
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

            // HandCardCanResolve performs the same visible mana precheck as the live router. Keep
            // this fixture explicitly able to pay for every 1-cost card so gesture assertions test
            // gestures rather than failing at the affordability gate.
            p0.Mana.Max = p0.Mana.Available = 10;
            p1.Mana.Max = p1.Mana.Available = 10;

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
                Rule("battlecry_damage", Trigger.ON_PLAY, 3, "ANY_CHARACTER", TargetSelection.Of(1)),
            };
            var anyCharacterCast = new[]
            {
                Rule("cast_damage", Trigger.ON_CAST, 1, "ANY_CHARACTER", TargetSelection.Of(1)),
            };
            var selfHeal = new[]
            {
                Rule("hero_power_damage", Trigger.ON_CAST, 1, "ENEMY_HERO", TargetSelection.Of(1)),
            };

            var friendlyReinforce = new[]
            {
                new RuleDefinition(new RuleId("herald_reinforce"), new TriggeredActivation(Trigger.ON_PLAY),
                    AlwaysCondition.Instance, OneShotLifetime.Instance,
                    new ModifyStatsAction(1, 1, new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1)))),
            };
            // A cost that buys nothing, which C28-COST-UNBOUGHT would reject on a real row. This
            // database is hand-built and never validated, and the gesture under test is whether a
            // required pick is DEMANDED rather than what it pays for — adding a gated sibling would
            // change the prompt this fixture exists to exercise.
            var friendlySacrifice = new[]
            {
                new RuleDefinition(new RuleId("herald_sacrifice"), new TriggeredActivation(Trigger.ON_PLAY),
                    AlwaysCondition.Instance, OneShotLifetime.Instance,
                    new DestroyAction(new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1),
                        cost: CostKind.Required))),
            };

            return new InMemoryCardDatabase(new[]
            {
                Def(Vanilla,        CardType.MINION,     System.Array.Empty<RuleDefinition>()),
                Def(TargetedSpell,  CardType.SPELL,      anyCharacterCast),
                Def(TargetedMinion, CardType.MINION,     anyCharacter),
                Def(FizzleMinion,   CardType.MINION,     friendlyReinforce),
                Def(CostMinion,     CardType.MINION,     friendlySacrifice),
                Def(PowerFree,      CardType.HERO_POWER, selfHeal),
                Def(PowerAim,       CardType.HERO_POWER, anyCharacterCast),
            });
        }

        private static RuleDefinition Rule(string id, Trigger trigger, int amount, string selector,
            TargetSelection selection)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(amount, new TargetSpec(selector, selection)));

        private static CardDefinition Def(int id, CardType type, IReadOnlyList<RuleDefinition> rules)
            => new CardDefinition(id, $"verify_{id}", $"verify_{id}", type, 1, 2, 3, null,
                                  CardClass.NEUTRAL, Rarity.COMMON,
                                  System.Array.Empty<Keyword>(), rules,
                                  "", "", SetId.CORE, true, null, "");

        private static void Check(string what, bool ok)
        {
            if (ok) { _passed++; Debug.Log($"[InputGestureVerify]   ok   {what}"); }
            else    { _failed++; Debug.LogError($"[InputGestureVerify]  FAIL  {what}"); }
        }
    }
}
