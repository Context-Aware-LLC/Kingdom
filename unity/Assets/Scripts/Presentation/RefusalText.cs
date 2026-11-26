using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The core's refusals in the player's language. A deck violation and a name check each carry
    /// an English sentence for the server and the logs, and a code with its arguments for this:
    /// <see cref="DeckViolation.Issue"/> and <see cref="NameCheck.Problem"/>. Card names come from
    /// <see cref="CardLoc"/> and class names from the glossary, so a German refusal names the card the
    /// player sees, not its English row.
    /// </summary>
    internal static class RefusalText
    {
        /// <summary>The first violation of <paramref name="queue"/>, said to the player, or "" when eligible.</summary>
        public static string Deck(QueueCheck queue, DeckList deck, ICardDatabase db)
        {
            var violations = queue.Validation.Violations;
            if (violations == null || violations.Count == 0) return "";
            return Deck(violations[0], deck, db);
        }

        public static string Deck(DeckViolation v, DeckList deck, ICardDatabase db)
        {
            switch (v.Issue)
            {
                case DeckIssue.InvalidPairing: return Loc.Get("deck_issue_invalid_pairing");
                case DeckIssue.AddCards: return Loc.Plural("deck_issue_add_cards", v.Amount, v.Amount);
                case DeckIssue.RemoveCards: return Loc.Plural("deck_issue_remove_cards", v.Amount, v.Amount);
                case DeckIssue.UnknownCard: return Loc.Get("deck_issue_unknown_card");
                case DeckIssue.Retired: return Loc.Format("deck_issue_retired", CardName(v.CardId, db));
                case DeckIssue.Unreleased: return Loc.Format("deck_issue_unreleased", CardName(v.CardId, db));
                case DeckIssue.TooManyCopies: return Loc.Format("deck_issue_too_many_copies", v.Amount, CardName(v.CardId, db));
                case DeckIssue.OffClass:
                    return deck == null
                        ? Loc.Format("deck_issue_off_class_short", CardName(v.CardId, db))
                        : Loc.Format("deck_issue_off_class", CardName(v.CardId, db),
                            GameTerms.Class(deck.PrimaryClass), GameTerms.Class(deck.SecondaryClass));
                case DeckIssue.NotOwned: return Loc.Format("deck_issue_not_owned", CardName(v.CardId, db));
                case DeckIssue.OwnsFewer: return Loc.Format("deck_issue_owns_fewer", v.Amount, CardName(v.CardId, db));
                default: return v.Message ?? "";
            }
        }

        /// <summary>Why <paramref name="check"/> refused the name, or "" when it did not.</summary>
        public static string Name(NameCheck check)
        {
            switch (check.Problem)
            {
                case NameProblem.None: return "";
                case NameProblem.Empty: return Loc.Get("name_issue_empty");
                case NameProblem.TooShort: return Loc.Format("name_issue_too_short", PlayerName.MinLength);
                case NameProblem.TooLong: return Loc.Format("name_issue_too_long", PlayerName.MaxLength);
                case NameProblem.Invisible: return Loc.Get("name_issue_invisible");
                case NameProblem.BadCharacter: return Loc.Format("name_issue_bad_character", check.Detail);
                case NameProblem.NoLetters: return Loc.Get("name_issue_no_letters");
                case NameProblem.Reserved:
                    string court = Loc.Find("name_court_" + check.Reserved.ToLowerInvariant());
                    return court == null
                        ? Loc.Format("name_issue_reserved", check.Detail)
                        : Loc.Format("name_issue_reserved_court", check.Detail, court);
                default: return check.Reason ?? "";
            }
        }

        /// <summary>
        /// A failed sign-in call, said to the player. Supabase's error code picks an
        /// <c>auth_error_&lt;code&gt;</c> row when one exists. Otherwise an English player reads
        /// Supabase's own words, which are often the most specific thing available, and anyone else
        /// reads <paramref name="fallback"/>, because those words are English. Offline is called out
        /// by name: it is the failure a first-launch sign-in makes most likely.
        /// </summary>
        public static string SignIn(AuthResult result, string fallback)
        {
            switch (result.Failure)
            {
                case AuthFailure.Network: return Loc.Get("signin_error_offline");
                case AuthFailure.NotConfigured: return Loc.Get("signin_error_not_configured");
                // Supabase's own wording here is "Signups not allowed for otp", which is true and
                // useless: it describes the request we made, not the thing the player got wrong.
                case AuthFailure.NoSuchAccount: return Loc.Get("signin_error_no_account");
                case AuthFailure.Malformed: return fallback;
            }
            string coded = string.IsNullOrEmpty(result.Code) ? null : Loc.Find("auth_error_" + result.Code);
            if (coded != null) return coded;
            return Loc.IsSource && !string.IsNullOrEmpty(result.Detail) ? result.Detail : fallback;
        }

        private static string CardName(int id, ICardDatabase db) =>
            db != null && db.TryGet(id, out CardDefinition def) ? CardLoc.Name(def, db) : "";
    }
}
