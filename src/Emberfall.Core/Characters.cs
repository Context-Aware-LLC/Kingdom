using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// A unified, allocation-free handle to a damageable/buffable character — either a board
    /// <see cref="CardInstance"/> (a minion) or a <see cref="HeroInstance"/>. The effect interpreter and
    /// the combat pipeline operate on characters uniformly; minions and heroes diverge only where a rule
    /// genuinely differs (armor soak, board death). Value type — never floats, never boxed on the hot path.
    /// </summary>
    internal readonly struct CharacterRef
    {
        public readonly int Seat;                 // owning seat (0/1) — canonical, fixed
        public readonly CardInstance? Minion;     // non-null for a board minion
        public readonly HeroInstance? Hero;       // non-null for a hero

        private CharacterRef(int seat, CardInstance? minion, HeroInstance? hero)
        {
            Seat = seat;
            Minion = minion;
            Hero = hero;
        }

        public static CharacterRef OfMinion(int seat, CardInstance m) => new CharacterRef(seat, m, null);
        public static CharacterRef OfHero(int seat, HeroInstance h) => new CharacterRef(seat, null, h);

        public bool IsHero => Hero != null;
        public bool IsMinion => Minion != null;

        public InstanceId Id => IsHero ? Hero!.Id : Minion!.Id;

        /// <summary>Resolves to the one <see cref="IEffectHost"/> this character is, collapsing the
        /// `if (IsMinion) … else if (IsHero) …` pair that used to precede every `.Effects` access on a
        /// character of unknown kind (effect-model-fork.md Phase 0.5 item 3). Null only for a
        /// default-constructed ref (neither field set).</summary>
        public IEffectHost? Host => (IEffectHost?)Minion ?? Hero;
    }

    /// <summary>Resolving an <see cref="InstanceId"/> to the live character it names (heroes + both boards).</summary>
    internal static class Characters
    {
        /// <summary>Find the character (hero or board minion) carrying <paramref name="id"/>, if any.</summary>
        public static bool TryFind(GameState state, InstanceId id, out CharacterRef character)
        {
            for (int seat = 0; seat < 2; seat++)
            {
                var ps = state.Players[seat];
                if (ps.Hero.Id == id)
                {
                    character = CharacterRef.OfHero(seat, ps.Hero);
                    return true;
                }
                var board = ps.Board;
                for (int i = 0; i < board.Count; i++)
                {
                    if (board[i].Id == id)
                    {
                        character = CharacterRef.OfMinion(seat, board[i]);
                        return true;
                    }
                }
            }
            character = default;
            return false;
        }
    }
}
