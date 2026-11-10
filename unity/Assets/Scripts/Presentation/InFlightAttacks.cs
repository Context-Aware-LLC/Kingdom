using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Attackers whose swing has been submitted and not yet answered. Same class of bug as a played
    /// card (<c>099c0854</c>): until the snapshot lands the mirror still shows the attacker with its
    /// attack unspent, so the courtesy validator passes a re-drag and every re-drag queued another
    /// swing behind the first (Sam re-dragged one three times while the server was slow). So an
    /// attacker is not pickable again until the authority answers.
    ///
    /// An attack is answered when the attacker's attacks-used count rises, the attacker leaves our
    /// board, or the turn moves on (<see cref="Prune"/>); or when the authority refuses it
    /// (<see cref="Release"/>), since a refusal emits no snapshot. A second legal swing (Windfury)
    /// is pickable again as soon as the first one's snapshot lands. Unity-free, so the Core tests
    /// compile it.
    /// </summary>
    public sealed class InFlightAttacks
    {
        private readonly struct Waiting
        {
            public readonly int UsedAtSubmit;
            public readonly int TurnAtSubmit;

            public Waiting(int usedAtSubmit, int turnAtSubmit)
            {
                UsedAtSubmit = usedAtSubmit;
                TurnAtSubmit = turnAtSubmit;
            }
        }

        private readonly Dictionary<InstanceId, Waiting> _waiting = new Dictionary<InstanceId, Waiting>();

        public int Count => _waiting.Count;

        public bool Contains(InstanceId attacker) => _waiting.ContainsKey(attacker);

        /// <summary>Records a submitted swing, judged against the mirror it was submitted on. An
        /// attacker the mirror cannot find is not recorded: nothing could ever answer it.</summary>
        public void Mark(InstanceId attacker, GameState mirror, PlayerId owner)
        {
            if (mirror == null) return;
            if (TryAttacksUsed(mirror, owner, attacker, out int used))
                _waiting[attacker] = new Waiting(used, mirror.TurnNumber);
        }

        /// <summary>The authority refused this attacker's swing.</summary>
        public void Release(InstanceId attacker) => _waiting.Remove(attacker);

        public void Clear() => _waiting.Clear();

        /// <summary>Drops every swing the snapshot answers.</summary>
        public void Prune(GameState state, PlayerId owner)
        {
            if (_waiting.Count == 0 || state == null) return;
            var answered = new List<InstanceId>();
            foreach (KeyValuePair<InstanceId, Waiting> entry in _waiting)
            {
                bool done = state.TurnNumber != entry.Value.TurnAtSubmit
                    || !TryAttacksUsed(state, owner, entry.Key, out int used)
                    || used > entry.Value.UsedAtSubmit;
                if (done) answered.Add(entry.Key);
            }
            foreach (InstanceId id in answered) _waiting.Remove(id);
        }

        private static bool TryAttacksUsed(GameState state, PlayerId owner, InstanceId attacker, out int used)
        {
            used = 0;
            foreach (PlayerState ps in state.Players)
            {
                if (ps.Id != owner) continue;
                if (ps.Hero != null && ps.Hero.Id.Equals(attacker))
                {
                    used = ps.HeroAttacksUsedThisTurn;
                    return true;
                }
                foreach (CardInstance card in ps.Board)
                {
                    if (!card.Id.Equals(attacker)) continue;
                    used = card.AttacksUsedThisTurn;
                    return true;
                }
            }
            return false;
        }
    }
}
