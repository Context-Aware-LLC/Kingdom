using System;
using System.Collections.Generic;

namespace Kingdom.Net.Protocol
{
    /// <summary>
    /// What a player reads about one achievement: its name, the plain line saying how it is
    /// earned, and the Council's citation. Shared because both ends render it — the server hands
    /// Play Console its description, and the client prints the proclamation and the Ledger — and
    /// two copies of the same sentence are two chances to disagree.
    /// </summary>
    /// <param name="Id">Permanent: it keys the earned row and the store entry, and neither store
    /// lets a published achievement be deleted, so an id is retired, never reused.</param>
    /// <param name="IsFinding">One of the Council's Findings — a bad idea, stamped on the result
    /// screen whenever a match commits it. The four gentle ones are not: winning a match is not a
    /// bad idea, and stamping it on every win would bury the ones that are.</param>
    public sealed record AchievementText(string Id, string Name, string Plain, string Citation, bool IsFinding)
    {
        /// <summary>The plain line, then the citation: Play Console's description.</summary>
        public string Description => Plain + " " + Citation;
    }

    /// <summary>
    /// Every achievement's text, in the order the Ledger lists them. The server's
    /// <c>AchievementCatalog</c> pairs each id with the rule that earns it; an entry here with no
    /// rule there fails <c>AchievementTests</c>.
    /// </summary>
    public static class AchievementTexts
    {
        public static IReadOnlyList<AchievementText> All { get; } = Array.AsReadOnly(new[]
        {
            Gentle("second_thoughts", "Second Thoughts",
                "Replace a card in your mulligan.", "The Council has also reconsidered."),
            Gentle("working_as_intended", "Working as Intended",
                "Use your Hero Power.", "The Council has inspected the button and found it to be working."),
            Gentle("early_retirement", "Early Retirement",
                "Destroy an enemy minion.", "Its pension has been approved."),
            Gentle("against_advice", "Against Advice",
                "Win a match.", "The Council has amended its advice."),
            Finding("proceed_unwisely", "Proceed, Unwisely",
                "Win a match with 1 Health.", "The Council had already drafted the other announcement."),
            Finding("out_of_ideas", "Out of Ideas",
                "Draw from an empty deck.", "The Council knows the feeling."),
            Finding("too_many_ideas", "Too Many Ideas",
                "Draw a card into a full hand.", "The card has been filed with the others."),
            Finding("approved_by_default", "Approved by Default",
                "Let the hourglass run out.", "The stamp went ahead without you."),
            Finding("waste_not", "Waste Not",
                "End a turn with 10 mana unspent.", "The Council admires the restraint and has asked what it was for."),
            Finding("mutual_respect", "Mutual Respect",
                "Draw a match.", "The Council is still deliberating."),
            Finding("self_starter", "Self-Starter",
                "Deal 15 damage to your own hero in one match.", "Pyra has asked for your name."),
            Finding("overkill", "Overkill",
                "Deal 10 damage to a minion with 1 Health.", "It has been thoroughly informed."),
            Finding("hubris", "Hubris",
                "Have a minion of yours costing 8 or more returned to your hand.", "It had only just unpacked."),
            Finding("gift_horse", "Gift Horse",
                "Lose control of your Legendary to the other side.", "It writes, occasionally."),
            Finding("seven_walls", "Seven Walls",
                "Have seven Bulwark minions at once.", "Hodge would like to see the plans."),
            Finding("the_last_idea", "The Last Idea",
                "Lose to an empty deck.", "It was a good plan. It was in there somewhere."),
            Finding("technically", "Technically",
                "Win a match with your hero at 0 Health or below.", "The Council has ruled that it counts."),
            Finding("posthumous", "Posthumous",
                "Win with damage from a minion's Toll.", "The deceased has been informed."),
            Finding("return_to_sender", "Return to Sender",
                "Win with a minion you took from your opponent.", "They would like it back."),
            Finding("signature_move", "Signature Move",
                "Finish the enemy hero with your Hero Power.", "The Council had assumed it was decorative."),
            Finding("own_goal", "Own Goal",
                "Lose to your own spell.", "Pyra was very impressed."),
            Finding("comeback", "Comeback",
                "Win a match after falling 20 Health behind the enemy hero.",
                "The Council's earlier remarks have been struck from the record."),
            Finding("the_long_game", "The Long Game",
                "Win a match on turn 40 or later.", "The Council has sent out for supper twice."),
            Finding("overgrown", "Overgrown",
                "Have a minion whose Attack and Health add up to 30 or more.", "Hodge has been asked to widen the doors."),
        });

        /// <summary>The text for this id, or null for one this build does not know.</summary>
        public static AchievementText? Find(string id)
        {
            foreach (AchievementText text in All)
                if (string.Equals(text.Id, id, StringComparison.Ordinal)) return text;
            return null;
        }

        private static AchievementText Gentle(string id, string name, string plain, string citation)
            => new AchievementText(id, name, plain, citation, IsFinding: false);

        private static AchievementText Finding(string id, string name, string plain, string citation)
            => new AchievementText(id, name, plain, citation, IsFinding: true);
    }
}
