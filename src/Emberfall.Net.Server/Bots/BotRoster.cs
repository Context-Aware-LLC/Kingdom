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
    /// account identity is resolved separately by <see cref="BotAccountProvisioner"/>.</summary>
    public sealed record BotRosterEntry(
        string AccountUsername,
        CardClass PrimaryClass,
        CardClass SecondaryClass,
        IReadOnlyList<string> Cards);

    /// <summary>Loads and validates <see cref="BotRosterEntry"/> rows from the canonical
    /// <c>bot-roster.json</c>. Validation happens once, at load time, so a malformed roster fails
    /// server startup loudly rather than producing an illegal deck at match time.</summary>
    public static class BotRoster
    {
        private sealed class Dto
        {
            public string AccountUsername { get; set; } = "";
            public CardClass PrimaryClass { get; set; }
            public CardClass SecondaryClass { get; set; }
            public List<string> Cards { get; set; } = new List<string>();
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
            var entries = new List<BotRosterEntry>(dtos.Count);
            foreach (var dto in dtos)
            {
                if (string.IsNullOrWhiteSpace(dto.AccountUsername))
                    throw new InvalidOperationException($"Bot roster '{path}' has an entry with no accountUsername.");
                if (!seenUsernames.Add(dto.AccountUsername))
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': duplicate accountUsername '{dto.AccountUsername}'.");

                // The account username doubles as the seat's DisplayName (BotAccountProvisioner), so
                // it must clear the SAME check every peer-chosen name clears (VULN-002 §4.1) — a
                // future entry over PlayerName.MaxLength or matching ReservedNames would otherwise
                // reach a human's versus screen unchecked. Loudly-fail-at-startup, consistent with
                // every other rule in this loader.
                var nameCheck = PlayerName.Check(dto.AccountUsername);
                if (!nameCheck.IsValid)
                    throw new InvalidOperationException(
                        $"Bot roster '{path}': entry '{dto.AccountUsername}' is not a valid display name: " +
                        nameCheck.Reason);

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
                foreach (string cardId in dto.Cards)
                {
                    if (!db.TryGet(cardId, out _))
                        throw new InvalidOperationException(
                            $"Bot roster '{path}': entry '{dto.AccountUsername}' references unknown " +
                            $"card id '{cardId}'.");
                }

                // Deck legality (DeckValidator L1-L5 via QueueEligibility.Check) — the SAME gate a
                // human's SubmitDeckMsg is run through in DeckSubmission.Validate. A bot roster needs
                // no ownership source (L6 is opt-in and never applies here): every id already resolved
                // above, so this is purely size/copy-cap/class-membership. A malformed/illegal roster
                // must fail server startup loudly rather than produce an illegal deck at match time —
                // this loader's whole reason to exist.
                var summed = new Dictionary<string, int>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (string cardId in dto.Cards)
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

                entries.Add(new BotRosterEntry(dto.AccountUsername, dto.PrimaryClass, dto.SecondaryClass, dto.Cards));
            }

            return entries;
        }
    }
}
