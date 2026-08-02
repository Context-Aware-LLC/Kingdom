using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Tools.CollectionSelfCheck
{
    /// <summary>
    /// EF-046 DONE-WHEN: create a character, build a legal 30-card deck, save, reload it from disk
    /// <b>in a fresh session</b>, and report queue-eligible.
    ///
    /// <para>Two phases, meant to be run as two separate processes against one <c>--root</c>:
    /// <c>--phase build</c> writes, <c>--phase verify</c> re-reads. Nothing is carried between them
    /// but the files on disk, which is the whole point — a single process holding a store instance
    /// open cannot honestly demonstrate a reload.</para>
    ///
    /// <para>Exit code is the verdict: 0 pass, 1 fail, 2 bad usage.</para>
    /// </summary>
    internal static class Program
    {
        // Fixed so the two phases agree without passing state between them. The account layer mints
        // real GUIDs (PlayerAccount.NewId); this check pins them so `verify` knows what to look for.
        private const string StorageKey = "ef046-selfcheck";
        private const string CharacterId = "ef046-character";
        private const string DeckId = "ef046-deck";
        private const string CharacterName = "Selfcheck Ashwarden";

        // The name `build` writes and `verify` must read back — proves rename persistence, which the
        // deckbuilder's name field relies on (DeckbuilderController.WireChrome -> SaveDeck).
        private const string RenamedTo = "Reloaded From Disk";

        private const ulong Seed = 0xEF046UL;

        private static int Main(string[] args)
        {
            string? root = null, phase = null, cardsDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root" when i + 1 < args.Length: root = args[++i]; break;
                    case "--phase" when i + 1 < args.Length: phase = args[++i]; break;
                    case "--cards" when i + 1 < args.Length: cardsDir = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"Unrecognised argument '{args[i]}'.");
                        return Usage();
                }
            }

            if (root == null || phase == null) return Usage();

            try
            {
                cardsDir ??= CardSet.DefaultCardDataDir();
                var db = CardSet.Load(cardsDir, out var files);

                string scope = CollectionPaths.ScopeDirectory(root, StorageKey);
                Directory.CreateDirectory(scope);

                Console.WriteLine($"card sets      : {string.Join(", ", files)}  ({db.Count} rows)");
                Console.WriteLine($"collection root: {root}");
                Console.WriteLine($"scope dir      : {scope}");
                Console.WriteLine();

                return phase switch
                {
                    "build" => Build(root, db),
                    "verify" => Verify(root, db),
                    _ => Usage(),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL  {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine(
                "usage: CollectionSelfCheck --root <dir> --phase <build|verify> [--cards <dir>]");
            return 2;
        }

        // Stores are constructed per phase and never shared. JsonCharacterStore/JsonDeckStore take a
        // file path; CollectionPaths derives it exactly as PlayerAccount does, so this writes the
        // same layout the game reads.
        private static ICharacterStore Characters(string root) => new JsonCharacterStore(CollectionPaths.Characters(root, StorageKey));
        private static IDeckStore Decks(string root) => new JsonDeckStore(CollectionPaths.Decks(root, StorageKey));

        // ---------------------------------------------------------------- build

        private static int Build(string root, CardSet db)
        {
            Console.WriteLine("== phase: build ==");

            var characterStore = Characters(root);
            var deckStore = Decks(root);

            // Same call the character-create screen makes: mints the character, generates a legal 30
            // from the pairing's pool, saves the deck, points DefaultDeckId at it, saves the character.
            var character = CharacterCreation.Create(
                characterStore, deckStore, db, new DeterministicRandom(Seed),
                CharacterId, CharacterName, CardClass.EMBERWARD, CardClass.TIDECALLER, DeckId);

            Console.WriteLine($"created character : {character.Name} [{character.Id}]");
            Console.WriteLine($"  pairing         : {character.PrimaryClass}/{character.SecondaryClass}");
            Console.WriteLine($"  DefaultDeckId   : {character.DefaultDeckId}");

            var generated = FindDeck(deckStore.LoadAll(), DeckId);
            if (generated == null) return Fail($"generator did not persist a deck with id '{DeckId}'.");

            Console.WriteLine($"generated deck    : '{generated.Name}' — {generated.TotalCount} cards, {generated.Entries.Count} distinct");

            // Now exercise the deckbuilder's own EDIT path, not just the generator's. Removing a copy
            // and putting a different legal card back through DeckValidator.CanAdd is exactly the swap
            // the collection grid performs, and CanAdd is the predicate the ticket names as the API to
            // build against. Without this the check would only ever prove CharacterCreation works.
            var victim = generated.Entries[0];
            generated.Entries[0] = victim.With(victim.Count - 1);
            if (generated.Entries[0].Count == 0) generated.Entries.RemoveAt(0);
            Console.WriteLine($"removed 1x        : {victim.CardId} -> {generated.TotalCount} cards");

            CardDefinition? replacement = null;
            foreach (var def in Ordered(db.AllCollectible()))
            {
                if (def.Id == victim.CardId) continue;
                if (!DeckValidator.CanAdd(generated, character, def)) continue;
                replacement = def;
                break;
            }

            if (replacement == null)
                return Fail("no collectible card passed DeckValidator.CanAdd for the 30th slot.");

            generated.Entries.Add(new DeckEntry(replacement.Id, 1));
            Console.WriteLine($"added 1x (CanAdd) : {replacement.Id} -> {generated.TotalCount} cards");

            var edited = DeckValidator.Validate(generated, character, db);
            if (!edited.IsValid)
                return Fail("the edited deck does not validate — the swap produced an illegal deck.");
            Console.WriteLine($"edited deck       : LEGAL at {generated.TotalCount} cards");

            // Then the deckbuilder's save path: rename, then persist as-is. This is literally what
            // DeckbuilderController.SaveDeck does (_account.Save(_deck) -> _decks.Save).
            generated.Name = RenamedTo;
            deckStore.Save(generated);
            Console.WriteLine($"renamed + saved   : '{generated.Name}'");

            var check = QueueEligibility.Check(generated, character, db);
            Console.WriteLine($"eligibility (pre-reload): {(check.IsEligible ? "ELIGIBLE" : "DRAFT — " + check.Reason)}");

            Console.WriteLine();
            Console.WriteLine("files on disk:");
            foreach (var f in Directory.GetFiles(CollectionPaths.ScopeDirectory(root, StorageKey)))
                Console.WriteLine($"  {Path.GetFileName(f),-20} {new FileInfo(f).Length,7} bytes");

            Console.WriteLine();
            Console.WriteLine("build OK — nothing is held open; re-run with --phase verify.");
            return 0;
        }

        // --------------------------------------------------------------- verify

        private static int Verify(string root, CardSet db)
        {
            Console.WriteLine("== phase: verify (fresh process, nothing carried over but the files) ==");

            // Brand new store objects in a brand new process: everything below came off disk.
            var character = FindCharacter(Characters(root).LoadAll(), CharacterId);
            if (character == null)
                return Fail($"no character '{CharacterId}' on disk — did --phase build run against this --root?");

            Console.WriteLine($"loaded character  : {character.Name} [{character.Id}]");
            Console.WriteLine($"  pairing         : {character.PrimaryClass}/{character.SecondaryClass}");
            Console.WriteLine($"  DefaultDeckId   : {character.DefaultDeckId}");

            // Resolve the deck through DefaultDeckId rather than the constant, so this also proves the
            // pointer the character carries is the one that actually round-tripped.
            var deck = FindDeck(Decks(root).LoadAll(), character.DefaultDeckId);
            if (deck == null) return Fail($"character's DefaultDeckId '{character.DefaultDeckId}' resolves to no deck on disk.");

            Console.WriteLine($"loaded deck       : '{deck.Name}' — {deck.TotalCount} cards, {deck.Entries.Count} distinct");
            Console.WriteLine($"  CharacterId     : {deck.CharacterId}");

            var failures = new List<string>();

            if (deck.Name != RenamedTo)
                failures.Add($"rename did not persist: expected '{RenamedTo}', got '{deck.Name}'.");

            if (deck.CharacterId != character.Id)
                failures.Add($"deck belongs to '{deck.CharacterId}', not '{character.Id}' (L5).");

            if (deck.TotalCount != DeckValidator.DeckSize)
                failures.Add($"deck is {deck.TotalCount} cards, expected {DeckValidator.DeckSize}.");

            // Every card id must still resolve, and must be legal for this pairing — the reload path
            // is where a stale id would surface (ICollectionStore: never silently drop or auto-repair).
            foreach (var e in deck.Entries)
            {
                if (!db.TryGet(e.CardId, out var def)) { failures.Add($"card '{e.CardId}' is not in the live set."); continue; }
                if (!character.AllowsClass(def.Class)) failures.Add($"'{def.Name}' ({def.Class}) is off-pairing for {character.PrimaryClass}/{character.SecondaryClass}.");
            }

            var validation = DeckValidator.Validate(deck, character, db);
            Console.WriteLine();
            Console.WriteLine($"DeckValidator     : {(validation.IsValid ? "LEGAL" : "ILLEGAL")}");
            foreach (var v in validation.Violations) Console.WriteLine($"  violation       : {v.Message}");
            if (!validation.IsValid) failures.Add("deck did not validate after reload.");

            // Reason only speaks for a draft; an eligible deck has nothing to explain.
            var check = QueueEligibility.Check(deck, character, db);
            Console.WriteLine($"QueueEligibility  : {(check.IsEligible ? "ELIGIBLE" : "DRAFT — " + check.Reason)}");
            if (!check.IsEligible) failures.Add($"deck is not queue-eligible after reload: {check.Reason}");

            Console.WriteLine();
            if (failures.Count > 0)
            {
                foreach (var f in failures) Console.Error.WriteLine($"FAIL  {f}");
                Console.Error.WriteLine($"\nSELF-CHECK FAILED ({failures.Count} problem(s)).");
                return 1;
            }

            Console.WriteLine("SELF-CHECK PASSED — character created, legal 30 built, saved, reloaded from disk");
            Console.WriteLine("                    in a fresh process, and queue-eligible.");
            return 0;
        }

        // --------------------------------------------------------------- helpers

        /// <summary>
        /// Collectibles in a stable id order. <see cref="ICardDatabase.AllCollectible"/> is backed by a
        /// dictionary, so picking "the first card that passes CanAdd" straight off it would depend on
        /// hash order — the swap would vary between runs and the check would flake.
        /// </summary>
        private static List<CardDefinition> Ordered(IReadOnlyCollection<CardDefinition> defs)
        {
            var list = new List<CardDefinition>(defs);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        private static Character? FindCharacter(IReadOnlyList<Character> roster, string id)
        {
            foreach (var c in roster) if (c.Id == id) return c;
            return null;
        }

        private static DeckList? FindDeck(IReadOnlyList<DeckList> decks, string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var d in decks) if (d.Id == id) return d;
            return null;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"FAIL  {message}");
            return 1;
        }
    }
}
