using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// EF-048 named verify: drives every transition of the press/drag/release machine
    /// (<see cref="DragTargetingModel"/>) against synthetic game states, with NO scene, NO camera and
    /// NO pointer hardware.
    ///
    /// <para>This is possible at all because EF-048 split the machine in two: the Unity half
    /// (pointer sampling, one raycast, the aim line) lives in <c>InputRouter</c>, and every decision
    /// about whether a gesture is legal and what intent it produces lives in the model. Input used to
    /// be the one system in this project that could only be checked by playing it.</para>
    ///
    /// <para>Run via the menu item, or headless through
    /// <see cref="HeadlessVerify.RunAll"/> / <c>tools/ci/unity-verify.ps1</c>.</para>
    /// </summary>
    public static class InputGestureVerify
    {
        private const string Vanilla   = "c_vanilla";     // a minion with no chosen target
        private const string Targeted  = "c_targeted";    // ON_PLAY + ANY_CHARACTER
        private const string PowerFree = "hp_untargeted"; // hero power, no chosen target
        private const string PowerAim  = "hp_targeted";   // hero power, ANY_CHARACTER

        private static readonly PlayerId P0 = new PlayerId("p0");
        private static readonly PlayerId P1 = new PlayerId("p1");

        private static int _passed;
        private static int _failed;

        [MenuItem("Emberfall/Verify Input Gestures (EF-048 drag targeting)")]
        public static void Run()
        {
            _passed = 0;
            _failed = 0;

            MulliganIsNotTurnGated();
            PressLegality();
            HandCardDrops();
            AttackDrops();
            HeroPowerDrops();
            NoAutoPlace();
            TargetSets();

            Debug.Log($"[InputGestureVerify] {_passed} passed, {_failed} failed");
            if (_failed > 0)
                Debug.LogError($"[InputGestureVerify] FAILED — {_failed} assertion(s)");
            else
                Debug.Log("[InputGestureVerify] GREEN — press/drag/release machine behaves as specified.");
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

            InstanceId hand    = s.Players[0].Hand[0].Id;   // Vanilla
            InstanceId aimCard = s.Players[0].Hand[1].Id;   // Targeted
            InstanceId mine    = s.Players[0].Board[0].Id;
            InstanceId theirs  = s.Players[1].Board[0].Id;

            Check("press on our hand card starts a HandCard drag",
                m.BeginSource(PointerSubject.OfCard(hand, P0, Zone.Hand)).Kind == DragSourceKind.HandCard);
            Check("a vanilla hand card needs no target",
                !m.BeginSource(PointerSubject.OfCard(hand, P0, Zone.Hand)).NeedsTarget);
            Check("an ANY_* hand card needs a target",
                m.BeginSource(PointerSubject.OfCard(aimCard, P0, Zone.Hand)).NeedsTarget);
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
            Check("drop of a vanilla card on our own minion plays nothing",
                m.Resolve(vanilla, PointerSubject.OfCard(mine, P0, Zone.Board)) == null);

            var atEnemy = m.Resolve(vanilla, PointerSubject.OfCard(theirs, P1, Zone.Board)) as PlayCardIntent;
            Check("drop on an enemy minion aims there", atEnemy != null && atEnemy.Targets.Count == 1
                                                        && atEnemy.Targets[0].Instance.Equals(theirs));

            var atFriend = m.Resolve(targeted, PointerSubject.OfCard(mine, P0, Zone.Board)) as PlayCardIntent;
            Check("an ANY_* card CAN aim at our own minion", atFriend != null
                                                             && atFriend.Targets[0].Instance.Equals(mine));

            var atHero = m.Resolve(targeted, PointerSubject.OfHero(m.HeroIdOf(P1), P1)) as PlayCardIntent;
            Check("a hand card can aim at a hero", atHero != null
                                                   && atHero.Targets[0].Instance.Equals(m.HeroIdOf(P1)));
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
            Check("with no taunt, the enemy hero is attackable", plain.Contains(m.HeroIdOf(P1)));
            Check("with no taunt, an enemy minion is attackable", plain.Contains(s.Players[1].Board[0].Id));

            // A taunt wall restricts the set to taunts and closes the face.
            CardInstance taunt = Minion(P1, 20);
            taunt.Keywords.Add(Keyword.TAUNT);
            s.Players[1].Board.Add(taunt);

            HashSet<InstanceId> walled = m.TargetsFor(m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board)));
            Check("a taunt wall closes the face", !walled.Contains(m.HeroIdOf(P1)));
            Check("a taunt wall excludes the non-taunt minion", !walled.Contains(s.Players[1].Board[0].Id));
            Check("a taunt wall includes the taunt", walled.Contains(taunt.Id));

            // Stealth hides a minion from targeting entirely.
            s.Players[1].Board.Remove(taunt);
            s.Players[1].Board[0].Keywords.Add(Keyword.STEALTH);
            HashSet<InstanceId> hidden = m.TargetsFor(m.BeginSource(PointerSubject.OfCard(mine, P0, Zone.Board)));
            Check("a stealthed enemy minion is not attackable", !hidden.Contains(s.Players[1].Board[0].Id));

            Check("a vanilla hand card asks for no target set",
                m.TargetsFor(m.BeginSource(PointerSubject.OfCard(s.Players[0].Hand[0].Id, P0, Zone.Hand))) == null);
        }

        // ── Fixtures ────────────────────────────────────────────────────────────────

        private static DragTargetingModel Model(GameState s)
            => new DragTargetingModel(P0, Db()) { State = s };

        // Our Main turn, one vanilla + one targeted card in hand, one minion each on board.
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

            p0.Hand.Add(new CardInstance(new InstanceId(10), Vanilla,  P0) { Zone = Zone.Hand });
            p0.Hand.Add(new CardInstance(new InstanceId(11), Targeted, P0) { Zone = Zone.Hand });
            p0.Board.Add(Minion(P0, 12));
            p1.Board.Add(Minion(P1, 13));
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
                new EffectDef(Trigger.ON_PLAY, Emberfall.Core.Contracts.Action.DEAL_DAMAGE, 3, null,
                              null, null, null, selector: "ANY_CHARACTER"),
            };
            var anyCharacterCast = new[]
            {
                new EffectDef(Trigger.ON_CAST, Emberfall.Core.Contracts.Action.DEAL_DAMAGE, 1, null,
                              null, null, null, selector: "ANY_CHARACTER"),
            };
            var selfHeal = new[]
            {
                new EffectDef(Trigger.ON_CAST, Emberfall.Core.Contracts.Action.DEAL_DAMAGE, 1, null,
                              null, null, null, selector: "ENEMY_HERO"),
            };

            return new InMemoryCardDatabase(new[]
            {
                Def(Vanilla,   CardType.MINION,     System.Array.Empty<EffectDef>()),
                Def(Targeted,  CardType.SPELL,      anyCharacter),
                Def(PowerFree, CardType.HERO_POWER, selfHeal),
                Def(PowerAim,  CardType.HERO_POWER, anyCharacterCast),
            });
        }

        private static CardDefinition Def(string id, CardType type, IReadOnlyList<EffectDef> effects)
            => new CardDefinition(id, id, type, 1, 2, 3, null,
                                  CardClass.NEUTRAL, Rarity.COMMON,
                                  System.Array.Empty<Keyword>(), effects,
                                  "", "", "", SetId.CORE, true, null, "", false);

        private static void Check(string what, bool ok)
        {
            if (ok) { _passed++; Debug.Log($"[InputGestureVerify]   ok   {what}"); }
            else    { _failed++; Debug.LogError($"[InputGestureVerify]  FAIL  {what}"); }
        }
    }
}
