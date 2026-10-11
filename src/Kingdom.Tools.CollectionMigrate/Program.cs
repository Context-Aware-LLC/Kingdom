using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Tools.CollectionMigrate
{
    /// <summary>
    /// ADR-0009's one-shot conversion, as a command. Two targets, run independently:
    ///
    /// <list type="bullet">
    /// <item><c>--local &lt;scope directory&gt;</c> — one player's files on one device.
    /// <c>decks.json</c> inherits the pairings out of <c>characters.json</c>, and
    /// <c>characters.json</c> is then deleted.</item>
    /// <item><c>--postgres &lt;connection string&gt;</c> — every row of <c>deck</c>, inheriting from
    /// the <c>character</c> table, matched on the deck's own <c>player_id</c>.</item>
    /// </list>
    ///
    /// <para><b>It reports and stops unless told <c>--apply</c>.</b> A migration whose default is to
    /// write is one that gets run by accident against the wrong directory; a migration whose default
    /// is to do nothing is one people run once, see no error, and believe worked. Printing the plan
    /// and requiring a word is what avoids both.</para>
    ///
    /// <para><b>Nothing is deleted before the replacement is on disk.</b> The local half writes
    /// <c>decks.json.pre-adr-0009.bak</c>, then the new <c>decks.json</c>, then removes
    /// <c>characters.json</c> — so a crash at any point leaves either the old pair or the new file
    /// plus a backup, never a decks.json that lost its pairings with nothing to recover them from.
    /// The Postgres half runs in one transaction for the same reason.</para>
    ///
    /// <para><b>DROPPING THE TABLE AND THE COLUMN IS A SEPARATE FLAG</b> (<c>--drop-legacy</c>), and
    /// deliberately not part of <c>--apply</c>. <c>schema.sql</c> is <c>create table if not
    /// exists</c> throughout and runs on every server start, so a <c>drop</c> could not live there —
    /// it would run for ever, and it would destroy rows a half-finished migration still needed.
    /// Dropping is this tool's job, once, after the conversion is verified.</para>
    ///
    /// <para>Exit code is the verdict: 0 ok, 1 failed, 2 bad usage.</para>
    /// </summary>
    internal static class Program
    {
        private const string BackupSuffix = ".pre-adr-0009.bak";

        private static async Task<int> Main(string[] args)
        {
            string? local = null;
            string? conn = null;
            bool apply = false;
            bool dropLegacy = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--local" when i + 1 < args.Length: local = args[++i]; break;
                    case "--postgres" when i + 1 < args.Length: conn = args[++i]; break;
                    case "--apply": apply = true; break;
                    case "--drop-legacy": dropLegacy = true; break;
                    default: return Usage($"unrecognised argument '{args[i]}'");
                }
            }

            if (local == null && conn == null)
                return Usage("nothing to migrate — pass --local and/or --postgres");
            if (dropLegacy && conn == null)
                return Usage("--drop-legacy is a Postgres-only step");
            if (dropLegacy && !apply)
                return Usage("--drop-legacy needs --apply; it is not something to rehearse");

            try
            {
                int code = 0;
                if (local != null) code |= MigrateLocal(local, apply);
                if (conn != null) code |= await MigratePostgresAsync(conn, apply, dropLegacy);
                return code == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAILED — {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine("Nothing was written.");
                return 1;
            }
        }

        private static int Usage(string problem)
        {
            Console.Error.WriteLine($"error: {problem}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("usage: CollectionMigrate [--local <scope dir>] [--postgres <conn>] [--apply] [--drop-legacy]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Without --apply it prints what it would do and writes nothing.");
            Console.Error.WriteLine("  --drop-legacy additionally drops the `character` table and deck.character_id.");
            Console.Error.WriteLine("  A scope dir is the folder holding decks.json — see CollectionPaths.ScopeDirectory.");
            return 2;
        }

        // ── One device ──────────────────────────────────────────────────────────────────

        private static int MigrateLocal(string scopeDirectory, bool apply)
        {
            string decksPath = Path.Combine(scopeDirectory, "decks.json");
            string charactersPath = Path.Combine(scopeDirectory, "characters.json");

            Console.WriteLine($"== local: {scopeDirectory} ==");

            if (!File.Exists(decksPath))
            {
                Console.WriteLine("  no decks.json — nothing to migrate.");
                // Still worth clearing a stranded roster file: it is the only thing left that
                // mentions characters, and leaving it invites a future reader to trust it.
                if (File.Exists(charactersPath) && apply)
                {
                    File.Delete(charactersPath);
                    Console.WriteLine("  removed characters.json (no decks referenced it).");
                }
                return 0;
            }

            string decksJson = File.ReadAllText(decksPath);
            string? charactersJson = File.Exists(charactersPath) ? File.ReadAllText(charactersPath) : null;
            if (charactersJson == null)
                Console.WriteLine("  no characters.json — any deck still missing a pairing is an orphan.");

            MigrationPlan plan = CollectionMigration.Plan(charactersJson, decksJson);
            Report(plan);

            if (!plan.ChangesAnything && charactersJson == null)
            {
                Console.WriteLine("  already migrated — nothing to do.");
                return 0;
            }

            if (!apply)
            {
                Console.WriteLine("  (dry run — pass --apply to write)");
                return 0;
            }

            // Backup first, then the replacement, then the roster. See the class doc: the order is
            // what makes every crash point recoverable.
            string backup = decksPath + BackupSuffix;
            File.Copy(decksPath, backup, overwrite: true);
            Console.WriteLine($"  backed up        : {Path.GetFileName(backup)}");

            File.WriteAllText(decksPath, plan.MigratedDecksJson);
            Console.WriteLine($"  wrote            : decks.json");

            if (File.Exists(charactersPath))
            {
                File.Delete(charactersPath);
                Console.WriteLine("  removed          : characters.json");
            }
            return 0;
        }

        private static void Report(MigrationPlan plan)
        {
            foreach (DeckMigrationRow d in plan.Decks)
            {
                string what = d.Outcome switch
                {
                    DeckOutcome.Inherited => $"inherit {d.Pairing}",
                    DeckOutcome.AlreadyMigrated => $"already {d.Pairing}",
                    _ => "DROP — its character is gone, so there is no pairing to inherit",
                };
                Console.WriteLine($"  {d.DeckId,-38} {d.CardCount,3} cards  '{d.Name}' — {what}");
            }

            Console.WriteLine($"  {plan.Count(DeckOutcome.Inherited)} inherited, " +
                              $"{plan.Count(DeckOutcome.AlreadyMigrated)} already migrated, " +
                              $"{plan.Count(DeckOutcome.Orphaned)} dropped.");
        }

        // ── The server ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every deck row, inheriting from the <c>character</c> table.
        ///
        /// <para><b>Matched on (character_id, player_id), never character_id alone.</b> Ids are
        /// globally unique but the pairing decides a deck's whole legal pool, and a cross-player
        /// match here would hand one player another's classes. That is the same predicate every
        /// query in <c>PostgresPlayerDataStore</c> carries, for the same reason.</para>
        ///
        /// <para>One transaction: a half-migrated <c>deck</c> table is a table where some rows read
        /// and some throw, which is worse than either end state.</para>
        /// </summary>
        private static async Task<int> MigratePostgresAsync(string connString, bool apply, bool dropLegacy)
        {
            Console.WriteLine("== postgres ==");

            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var pairings = new Dictionary<string, (string Primary, string Secondary)>(StringComparer.Ordinal);
            if (await TableExistsAsync(conn, tx, "character"))
            {
                await using var read = new NpgsqlCommand(
                    "select character_id, player_id, primary_class, secondary_class from character", conn, tx);
                await using var r = await read.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    pairings[Key(r.GetString(0), r.GetString(1))] = (r.GetString(2), r.GetString(3));
                Console.WriteLine($"  characters       : {pairings.Count}");
            }
            else
            {
                Console.WriteLine("  no `character` table — any deck still missing a pairing is an orphan.");
            }

            var rows = new List<(string DeckId, string PlayerId, string Body)>();
            await using (var read = new NpgsqlCommand("select deck_id, player_id, body from deck", conn, tx))
            await using (var r = await read.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));

            int inherited = 0, already = 0, dropped = 0;
            foreach (var (deckId, playerId, body) in rows)
            {
                string? charactersJson = CharactersDocFor(pairings, playerId);
                MigrationPlan plan = CollectionMigration.Plan(charactersJson, body);

                if (plan.Count(DeckOutcome.AlreadyMigrated) > 0) { already++; continue; }

                if (plan.Count(DeckOutcome.Orphaned) > 0)
                {
                    dropped++;
                    Console.WriteLine($"  DROP {deckId} (player {playerId}) — its character is gone.");
                    if (apply)
                    {
                        await using var del = new NpgsqlCommand(
                            "delete from deck where deck_id=$1 and player_id=$2", conn, tx);
                        del.Parameters.Add(P(deckId));
                        del.Parameters.Add(P(playerId));
                        await del.ExecuteNonQueryAsync();
                    }
                    continue;
                }

                inherited++;
                if (apply)
                {
                    await using var up = new NpgsqlCommand(
                        "update deck set body=$1, updated_at=now() where deck_id=$2 and player_id=$3", conn, tx);
                    up.Parameters.Add(P(plan.MigratedDecksJson));
                    up.Parameters.Add(P(deckId));
                    up.Parameters.Add(P(playerId));
                    await up.ExecuteNonQueryAsync();
                }
            }

            Console.WriteLine($"  decks            : {inherited} inherited, {already} already migrated, {dropped} dropped.");

            if (dropLegacy)
            {
                await using (var drop = new NpgsqlCommand(
                    "alter table deck drop column if exists character_id", conn, tx))
                    await drop.ExecuteNonQueryAsync();
                await using (var drop = new NpgsqlCommand("drop table if exists character", conn, tx))
                    await drop.ExecuteNonQueryAsync();
                Console.WriteLine("  dropped          : deck.character_id, table character");
            }

            if (!apply)
            {
                await tx.RollbackAsync();
                Console.WriteLine("  (dry run — pass --apply to write)");
                return 0;
            }

            await tx.CommitAsync();
            Console.WriteLine("  committed.");
            return 0;
        }

        /// <summary>
        /// The pairings visible to ONE player, in the shape <see cref="CollectionMigration"/> reads.
        /// Building a per-player document rather than passing the whole table is what enforces the
        /// (character_id, player_id) match inside a component that knows nothing about players.
        /// </summary>
        private static string? CharactersDocFor(
            Dictionary<string, (string Primary, string Secondary)> pairings, string playerId)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartArray("characters");
                foreach (var kv in pairings)
                {
                    int split = kv.Key.IndexOf('\0');
                    if (split < 0 || string.CompareOrdinal(kv.Key, split + 1, playerId, 0, playerId.Length) != 0)
                        continue;
                    w.WriteStartObject();
                    w.WriteString("id", kv.Key.Substring(0, split));
                    w.WriteString("primaryClass", kv.Value.Primary);
                    w.WriteString("secondaryClass", kv.Value.Secondary);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>A NUL between the two halves: neither a character id nor a player id can contain
        /// one, so the composite cannot be forged by a crafted id.</summary>
        private static string Key(string characterId, string playerId) => characterId + '\0' + playerId;

        private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table)
        {
            await using var cmd = new NpgsqlCommand("select to_regclass($1) is not null", conn, tx);
            cmd.Parameters.Add(P(table));
            return await cmd.ExecuteScalarAsync() is true;
        }

        private static NpgsqlParameter P(string value)
            => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = value };
    }
}
