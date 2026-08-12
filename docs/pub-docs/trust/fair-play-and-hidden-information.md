# Fair Play and Hidden Information

Emberfall treats every game client as an untrusted view of a match. The server owns the rules, randomness, clocks, and authoritative game state.

## What the server decides

The client sends an intended action, such as playing a card or declaring an attack. The server checks that the action is legal, applies it to the authoritative match, and sends each player an updated view. The client does not decide damage, targets, random results, or whether an action succeeded.

This also means the game does not rely on client-side prediction for card actions. What appears on screen follows the server's accepted result.

## Hidden information stays off the opponent's device

The server prepares a different view for each player before sending it:

- You receive the identities of cards in your hand; your opponent receives only card backs and the hand count.
- Both players may see how many cards remain in a deck, but neither client receives deck order.
- A private choice reveals its options only to the player making that choice. The opponent may see that a choice is in progress, not the options.
- Cards and effects in public zones, such as the board and graveyard, are visible to both players.

The same rules apply after reconnecting. Reconnection never unlocks a more trusted or less-redacted view.

## Randomness and audits

The server chooses and retains the match's random seed. A client never receives enough information to reconstruct future draws or the opponent's deck order.

Accepted actions are recorded in order so a match can be replayed deterministically for testing and dispute investigation. Integrity checks verify that a player's received snapshot was not corrupted in transit; they do not compare the two players' views, because those views intentionally contain different hidden information.
