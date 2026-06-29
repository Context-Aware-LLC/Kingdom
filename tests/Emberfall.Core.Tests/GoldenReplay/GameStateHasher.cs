using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// Canonical serializer + hasher (golden-replay.md §4). The hash is only meaningful if
    /// serialization is deterministic across machines, runtimes, and runs, so this:
    ///  • walks <see cref="GameState"/> in a FIXED, hand-specified field order (never reflection, §4.1);
    ///  • serializes only integers, enums (as backing int), length-prefixed UTF-8 strings, and the RNG
    ///    raw state — it has NO float code path (§4.2). A float in state cannot even be expressed here;
    ///    the banned-API reflection scan is the static twin that keeps floats out of state types;
    ///  • writes every integer LITTLE-ENDIAN via <see cref="BinaryPrimitives"/>, never BitConverter, so
    ///    the digest is identical on x64 dev boxes and ARM CI/servers (§4.4);
    ///  • folds <see cref="DeterministicRandom.RawState"/> into the hash (§4.3) — two states with the
    ///    same board but a different RNG cursor are NOT equal, catching "right now, wrong future" desyncs.
    /// Test-layer for M1; the scheme is a contract the Multiplayer Engineer reuses for StateDelta
    /// integrity (test-strategy.md §5 Q2).
    /// </summary>
    public static class GameStateHasher
    {
        public const string Algorithm = "sha256";

        /// <summary>Canonical bytes → SHA-256 → lowercase hex.</summary>
        public static string Hash(GameState state)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var w = new CanonicalWriter(sha);
            Walk(w, state);
            var digest = sha.GetHashAndReset();
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ── the canonical walk (declaration order from game-state-and-actions.md §2) ──

        private static void Walk(CanonicalWriter w, GameState s)
        {
            w.Str(s.Id.Value);
            w.U64(s.StateVersion);
            w.U64(s.Rng.RawState);                  // §4.3 — RNG cursor is part of the hash
            w.Str(s.ActivePlayer.Value);
            w.Enum(s.Phase);
            w.I32(s.TurnNumber);
            WalkPlayer(w, s.Players[0]);            // [0] then [1] — fixed seat order
            WalkPlayer(w, s.Players[1]);
            w.I32(s.FatigueCounter0);
            w.I32(s.FatigueCounter1);
            w.Enum(s.Outcome);
            w.I32(s.ActionResolutionsThisTurn);
            w.I32(s.NextInstanceCounter);
            w.U64(s.NextEventSequence);
            w.Bool(s.Mulligan0Done);
            w.Bool(s.Mulligan1Done);
        }

        private static void WalkPlayer(CanonicalWriter w, PlayerState p)
        {
            w.Str(p.Id.Value);
            WalkHero(w, p.Hero);
            WalkZone(w, p.Deck);                    // ordered lists — serialized in list order (§4.1)
            WalkZone(w, p.Hand);
            WalkZone(w, p.Board);
            WalkWeapon(w, p.Weapon);                // nullable
            WalkZone(w, p.Graveyard);
            WalkMana(w, p.Mana);
            w.Bool(p.HeroPowerUsedThisTurn);
            w.I32(p.HeroAttacksUsedThisTurn);
            w.I32(p.NextTurnOverloadLock);
        }

        private static void WalkHero(CanonicalWriter w, HeroInstance h)
        {
            w.I32(h.Id.Value);
            w.Str(h.Controller.Value);
            w.Enum(h.Class);
            w.I32(h.CurrentHealth);
            w.I32(h.MaxHealth);
            w.I32(h.Armor);
            w.Str(h.HeroPowerId);
        }

        private static void WalkZone(CanonicalWriter w, List<CardInstance> zone)
        {
            w.I32(zone.Count);
            foreach (var c in zone) WalkCard(w, c);
        }

        private static void WalkCard(CanonicalWriter w, CardInstance c)
        {
            w.I32(c.Id.Value);
            w.Str(c.DefinitionId);
            w.Str(c.Controller.Value);
            w.Enum(c.Zone);
            w.I32(c.CurrentAttack);
            w.I32(c.CurrentHealth);
            w.I32(c.MaxHealth);
            w.NullableI32(c.CurrentDurability);

            // The one unordered collection in state: impose a total order (sorted by enum int) so it
            // cannot perturb the hash (§4.1).
            var kws = new List<Keyword>(c.Keywords);
            kws.Sort((a, b) => ((int)a).CompareTo((int)b));
            w.I32(kws.Count);
            foreach (var k in kws) w.Enum(k);

            w.I32(c.Enchantments.Count);            // list order (§4.1)
            foreach (var e in c.Enchantments)
            {
                w.I32(e.Source.Value);
                w.I32(e.AttackDelta);
                w.I32(e.HealthDelta);
                w.NullableEnum(e.GrantedKeyword);
            }

            w.Bool(c.HasAegisShield);
            w.Bool(c.IsFrozen);
            w.Bool(c.SummonedThisTurn);
            w.I32(c.AttacksUsedThisTurn);
            w.Bool(c.HasUsedReborn);
            w.I32(c.Position);
        }

        private static void WalkWeapon(CanonicalWriter w, WeaponInstance? weapon)
        {
            if (weapon is null) { w.Bool(false); return; }
            w.Bool(true);
            w.I32(weapon.Id.Value);
            w.Str(weapon.DefinitionId);
            w.I32(weapon.Attack);
            w.I32(weapon.Durability);
        }

        private static void WalkMana(CanonicalWriter w, ManaPool m)
        {
            w.I32(m.Max);
            w.I32(m.Available);
            w.I32(m.Spent);
            w.I32(m.Locked);
            w.I32(m.Temporary);
        }

        /// <summary>Little-endian primitive writer feeding the incremental hash. No float method exists by design.</summary>
        private readonly struct CanonicalWriter
        {
            private readonly IncrementalHash _sha;
            public CanonicalWriter(IncrementalHash sha) => _sha = sha;

            public void I32(int v)
            {
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(b, v);
                _sha.AppendData(b);
            }

            public void U64(ulong v)
            {
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(b, v);
                _sha.AppendData(b);
            }

            public void Bool(bool v)
            {
                Span<byte> b = stackalloc byte[1];
                b[0] = v ? (byte)1 : (byte)0;
                _sha.AppendData(b);
            }

            public void Enum<TEnum>(TEnum v) where TEnum : struct, System.Enum
                => I32(Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));

            public void NullableEnum<TEnum>(TEnum? v) where TEnum : struct, System.Enum
            {
                if (v.HasValue) { Bool(true); Enum(v.Value); }
                else Bool(false);
            }

            public void NullableI32(int? v)
            {
                if (v.HasValue) { Bool(true); I32(v.Value); }
                else Bool(false);
            }

            public void Str(string? s)
            {
                if (s is null) { I32(-1); return; }      // distinguish null from empty
                var bytes = Encoding.UTF8.GetBytes(s);
                I32(bytes.Length);                        // length-prefixed UTF-8 (§4.2)
                _sha.AppendData(bytes);
            }
        }
    }
}
