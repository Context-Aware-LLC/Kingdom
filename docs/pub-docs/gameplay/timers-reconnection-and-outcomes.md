# Timers, Reconnection, and Match Outcomes

Match time is enforced by the server so both players follow the same clock.

## Match clocks

| Clock | Rule |
|---|---|
| Mulligan | 45 seconds; an unfinished mulligan is automatically confirmed when time expires |
| Turn | 75 seconds |
| Final warning | Begins with 15 seconds remaining |

When a turn expires, the server ends it automatically. One timed-out turn is not an immediate loss. Two consecutive turns auto-ended for the same player cause that player to forfeit. Any accepted voluntary action resets that player's idle count.

## If one player disconnects

The match keeps moving. The connected player continues taking normal turns, while the missing player's turns expire automatically.

A returning player may reconnect while the match still exists. Reconnection restores the current board state; it does not replay every animation or event that happened while the player was away. If the match cannot be found after reconnecting, it has already ended or been voided.

There is no separate disconnect grace timer and the opponent's clock is never paused. A disconnected player loses only through the same two-turn idle rule that applies to anyone who stops participating.

## If both players disconnect

When no player remains connected, the match is voided immediately. A void has no winner, no loser, and no ranking change. It is not recorded as a draw.
