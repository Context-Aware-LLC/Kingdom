using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Bots
{
    /// <summary>One bot identity's fixed class pairing + decklist, as drafted offline and reformatted
    /// into this repo's canonical shape (design doc §Bot roster & provisioning). Deck content only —
    /// account identity is resolved separately by <see cref="BotAccountProvisioner"/>.
    ///
    /// <para><b><see cref="AccountUsername"/> and <see cref="DisplayName"/> are not interchangeable,
    /// and the split is load-bearing.</b> The username is the account IDENTITY:
    /// <c>BotAccountProvisioner.DeriveCredentialHash</c> hashes it, so editing one in the roster
    /// re-mints that bot's account and strands the old one. The display name is the only half a
    /// human ever sees, so it is the half that clears <see cref="PlayerName"/> and the half to
    /// change when a bot should read differently on a nameplate.</para></summary>
    public sealed record BotRosterEntry(
        string AccountUsername,
        string DisplayName,
        CardClass PrimaryClass,
        CardClass SecondaryClass,
        IReadOnlyList<int> Cards);

    /// <summary>Loads and validates <see cref="BotRosterEntry"/> rows from the canonical
    /// <c>bot-roster.json</c>. Validation happens once, at load time, so a malformed roster fails
    /// server startup loudly rather than producing an illegal deck at match time.</summary>
    public static class BotRoster
    {
        private sealed class Dto
        {
            public string AccountUsername { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public CardClass PrimaryClass { get; set; }
            public CardClass SecondaryClass { get; set; }
            public List<int> Cards { get; set; } = new List<int>();
        }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        public static IReadOnlyList<BotRosterEntry> LoadFromFile(string path, ICardDatabase db)
        {
            string json = File.ReadAllText(path);
            List<Dto>? dtos;
            try
            {
                dtos = JsonSerializer.Deserialize<List<Dto>>(json, Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Bot roster '{path}' is not valid JSON: {ex.Message}", ex);
            }

            if (dtos is null || dtos.Count == 0)
                throw new InvalidOperationException($"Bot roster '{path}' is empty.");

            var seenUsernames = new HashSet<string>(StringComparer.Ordinal);
            var seenDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<BotRosterEntry>(dtos.Count);
            foreach (var dto in dtos)
            {
                if (string.IsNullOrWhiteSpace(dto.AccountUsername))
                    throw new InvalidOperationException($"Bot roster '{path}' has an entry with no accountUsername.");
                if (!seenUsernames.Add(dto.AccountUsername))
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': duplicate accountUsername '{dto.AccountUsername}'.");

                // displayName is REQUIRED, and deliberately has no fallback to accountUsername. It
                // used to BE the username (BotAccountProvisioner passed it for both), which is how
                // two bots shipped to human opponents wearing nameplates that read `ember_knight`
                // and `tide_warden` — the username is a key, and keys make unconvincing names. A
                // silent fallback would just reintroduce that one entry at a time.
                if (string.IsNullOrWhiteSpace(dto.DisplayName))
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': entry '{dto.AccountUsername}' has no displayName.");

                // The display name is what reaches a human's versus screen, so it clears the SAME
                // check every peer-chosen name clears (VULN-002 §4.1) — an entry over
                // PlayerName.MaxLength or matching ReservedNames would otherwise arrive unchecked
                // through a door that only inspects peers. Loudly-fail-at-startup, consistent with
                // every other rule in this loader. The username is NOT checked: it never reaches a
                // screen, and its charset is an identity concern rather than a display one.
                //
                // Store the NORMALIZED value, per PlayerName's own contract — otherwise a roster
                // authored with a stray double space stores different bytes than the same name typed
                // by a player.
                var nameCheck = PlayerName.Check(dto.DisplayName);
                if (!nameCheck.IsValid)
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': entry '{dto.AccountUsername}' has displayName " +
                        $"'{dto.DisplayName}', which is not a valid display name: " + nameCheck.Reason);
                string displayName = nameCheck.Normalized;

                // Case-insensitive, matching PlayerName.IsReserved's comparison: two bots that
                // differ only in case are indistinguishable on a nameplate, which is the surface
                // this name exists for.
                if (!seenDisplayNames.Add(displayName))
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': duplicate displayName '{displayName}'.");

                if (!Character.IsValidPairing(dto.PrimaryClass, dto.SecondaryClass))
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': entry '{dto.AccountUsername}' has an invalid class " +
                        $"pairing {dto.PrimaryClass}/{dto.SecondaryClass}.");

                // System.Text.Json deserializes an explicit "cards": null over the `= new()` field
                // initializer, so a null Cards list is reachable even though the DTO field default
                // reads otherwise — check for null explicitly rather than trusting the initializer.
                if (dto.Cards is null || dto.Cards.Count == 0)
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': entry '{dto.AccountUsername}' has no cards.");
                foreach (int cardId in dto.Cards)
                {
                    if (!db.TryGet(cardId, out _))
                        throw new InvalidOperationException(
                            $"Bot roster '{path}': entry '{dto.AccountUsername}' references unknown " +
                            $"card id {cardId}.");
                }

                // Deck legality (DeckValidator L1-L5 via QueueEligibility.Check) — the SAME gate a
                // human's SubmitDeckMsg is run through in DeckSubmission.Validate. A bot roster needs
                // no ownership source (L6 is opt-in and never applies here): every id already resolved
                // above, so this is purely size/copy-cap/class-membership. A malformed/illegal roster
                // must fail server startup loudly rather than produce an illegal deck at match time —
                // this loader's whole reason to exist.
                var summed = new Dictionary<int, int>();
                var order = new List<int>();
                foreach (int cardId in dto.Cards)
                {
                    if (!summed.TryGetValue(cardId, out int have)) order.Add(cardId);
                    summed[cardId] = have + 1;
                }

                var deck = new DeckList { Id = "bot:" + dto.AccountUsername, CharacterId = dto.AccountUsername, Name = dto.AccountUsername };
                foreach (var id in order) deck.Entries.Add(new DeckEntry(id, summed[id]));
                var character = new Character(dto.AccountUsername, dto.AccountUsername, dto.PrimaryClass, dto.SecondaryClass);

                var check = QueueEligibility.Check(deck, character, db);
                if (!check.IsEligible)
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': entry '{dto.AccountUsername}' has an illegal deck: {check.Reason}");

                entries.Add(new BotRosterEntry(
                    dto.AccountUsername, displayName, dto.PrimaryClass, dto.SecondaryClass, dto.Cards));
            }

            return entries;
        }
    }
}
