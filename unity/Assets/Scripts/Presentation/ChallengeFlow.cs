using System;
using System.Collections.Generic;
using Emberfall.Net.Client.Social;
using Emberfall.Net.Protocol;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Where this player is in a friend challenge (#409).</summary>
    public enum ChallengePhase
    {
        None,
        /// <summary>On the deck roster, choosing the deck to challenge with. Nothing sent yet.</summary>
        PickingToSend,
        /// <summary>Sent; the server has not confirmed it went out.</summary>
        Sending,
        /// <summary>The friend has it and has not answered.</summary>
        WaitingForAnswer,
        /// <summary>A friend is challenging this player.</summary>
        Incoming,
        /// <summary>On the deck roster, choosing the deck to accept with.</summary>
        PickingToAccept,
        /// <summary>Accepted; waiting for the server's match ticket.</summary>
        Starting,
    }

    /// <summary>
    /// The friend-challenge handshake on the client (#409), from the Challenge button to the match
    /// scene. Static because it outlives every screen it passes through: it starts in the friends
    /// drawer, detours through the deck roster, and ends in <see cref="NetworkedGameBootstrap"/>.
    ///
    /// <para><b>Both players pick their deck BEFORE their half goes out</b> (Sam, 2026-10-02): the
    /// sender before sending, the receiver before accepting. The server's ticket only lives
    /// 30 s after the accept (<c>ServerOptions.ChallengeArrivalTimeoutMs</c>), which leaves no time
    /// to choose a deck once it arrives.</para>
    ///
    /// <para><b>One challenge at a time.</b> An offer that arrives while this player is already in
    /// one, or on a screen with no friends drawer to show it, is declined on their behalf, so a
    /// friend is never left waiting on a prompt that cannot be shown.</para>
    ///
    /// <para>A friend is named by friend code on the wire; the name shown is looked up from the
    /// last friends list the drawer drew (<see cref="RememberNames"/>), and is peer-authored text.</para>
    /// </summary>
    public static class ChallengeFlow
    {
        private static bool _listening;
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);

        public static ChallengePhase Phase { get; private set; }

        /// <summary>The friend's code, canonical.</summary>
        public static string Code { get; private set; } = "";

        public static string ChallengeId { get; private set; } = "";

        /// <summary>When the current offer lapses, in <c>Time.realtimeSinceStartup</c>; 0 when unknown.</summary>
        public static float ExpiresAt { get; private set; }

        /// <summary>A one-line result for the player — a refusal, a decline, a lost connection.
        /// Empty when there is nothing to say. Cleared by <see cref="DismissNotice"/>.</summary>
        public static string Notice { get; private set; } = "";

        public static bool IsPicking => Phase == ChallengePhase.PickingToSend || Phase == ChallengePhase.PickingToAccept;

        /// <summary>Raised on the main thread whenever anything above changes.</summary>
        public static event Action Changed;

        private static string _ticket = "";

        /// <summary>Subscribe to the social connection. Idempotent; the drawer calls it on mount.</summary>
        public static void Listen()
        {
            if (_listening) return;
            _listening = true;
            SocialGateway.Received += OnSocial;
            SocialGateway.StateChanged += OnSocialState;
        }

        /// <summary>The friend's name if the drawer has drawn it, otherwise their code.</summary>
        public static string NameOf(string code) =>
            Names.TryGetValue(code, out string name) && name.Length > 0 ? name : FriendCode.Display(code);

        public static bool KnowsName(string code) => Names.ContainsKey(code);

        /// <summary>Does not raise <see cref="Changed"/>: the drawer calls it from its own redraw.</summary>
        public static void RememberNames(IEnumerable<FriendEntry> friends)
        {
            foreach (FriendEntry friend in friends) Names[friend.Code] = friend.DisplayName;
        }

        /// <summary>The single-use match ticket, cleared by the read. Empty for an ordinary queued match.</summary>
        public static string TakeTicket()
        {
            string ticket = _ticket;
            _ticket = "";
            return ticket;
        }

        // ── player actions ──────────────────────────────────────────────────────────────

        /// <summary>The Challenge button: off to the deck roster to choose a deck.</summary>
        public static void Challenge(string code)
        {
            if (Phase != ChallengePhase.None) return;
            Set(ChallengePhase.PickingToSend, code, "");
            MatchDeckSelection.OpenPicker();
        }

        /// <summary>Accept the incoming offer: off to the deck roster to choose a deck.</summary>
        public static void Accept()
        {
            if (Phase != ChallengePhase.Incoming) return;
            Phase = ChallengePhase.PickingToAccept;
            Raise();
            MatchDeckSelection.OpenPicker();
        }

        /// <summary>Decline an offer, or withdraw one this player sent.</summary>
        public static void Withdraw()
        {
            if (ChallengeId.Length > 0) _ = SocialGateway.SendAsync(new ChallengeCloseMsg(ChallengeId));
            Abort("");
        }

        /// <summary>The roster's Back, while picking. The sender had sent nothing; the receiver declines.</summary>
        public static void PickCancelled()
        {
            if (Phase == ChallengePhase.PickingToAccept) Withdraw();
            else if (Phase == ChallengePhase.PickingToSend) Abort("");
        }

        /// <summary>The roster's Continue, while picking. The deck is armed for the match scene and
        /// this player's half of the handshake goes out.</summary>
        public static async void DeckChosen(string deckId)
        {
            ChallengePhase picking = Phase;
            if (picking != ChallengePhase.PickingToSend && picking != ChallengePhase.PickingToAccept) return;

            MatchDeckSelection.Select(deckId);
            Phase = picking == ChallengePhase.PickingToSend ? ChallengePhase.Sending : ChallengePhase.Starting;
            Raise();

            SocialMsg half = picking == ChallengePhase.PickingToSend
                ? new ChallengeSendMsg(Code)
                : (SocialMsg)new ChallengeAcceptMsg(ChallengeId);
            bool sent;
            try { sent = await SocialGateway.SendAsync(half); }
            catch (Exception) { sent = false; }

            if (!sent) Abort("Not connected to friends right now. Try again in a moment.");
        }

        public static void DismissNotice()
        {
            if (Notice.Length == 0) return;
            Notice = "";
            Raise();
        }

        // ── the server ──────────────────────────────────────────────────────────────────

        private static void OnSocial(SocialMsg msg)
        {
            switch (msg)
            {
                case ChallengeIncomingMsg incoming:
                    // No drawer means a screen with nowhere to show the prompt (the match scene).
                    if (Phase != ChallengePhase.None || !FriendsDrawerController.IsMounted)
                    {
                        _ = SocialGateway.SendAsync(new ChallengeCloseMsg(incoming.ChallengeId));
                        return;
                    }
                    Set(ChallengePhase.Incoming, incoming.Code, incoming.ChallengeId, incoming.TtlMs);
                    break;

                case ChallengeSentMsg sent when Phase == ChallengePhase.Sending && sent.Code == Code:
                    Set(ChallengePhase.WaitingForAnswer, Code, sent.ChallengeId, sent.TtlMs);
                    break;

                case ChallengeRefusedMsg refused when Phase == ChallengePhase.Sending && refused.Code == Code:
                    // The server gives one answer for every cause on purpose; so does this.
                    Abort($"{NameOf(Code)} can't take a challenge right now.");
                    break;

                case ChallengeReadyMsg ready when ready.ChallengeId == ChallengeId &&
                                                  (Phase == ChallengePhase.WaitingForAnswer || Phase == ChallengePhase.Starting):
                    _ticket = ready.Ticket;
                    Clear();
                    Raise();
                    ScreenNav.Go(ScreenNav.BoardNetworked);
                    break;

                case ChallengeClosedMsg closed when closed.ChallengeId == ChallengeId:
                    Abort(Describe(closed.Reason));
                    break;
            }
        }

        private static void OnSocialState(SocialState state)
        {
            if (state == SocialState.Live || Phase == ChallengePhase.None || Phase == ChallengePhase.PickingToSend) return;
            // The server drops an offer whose player's socket went away; say so rather than wait.
            Abort("Lost the connection to friends. The challenge was cancelled.");
        }

        private static string Describe(ChallengeCloseReason reason)
        {
            string name = NameOf(Code);
            return reason switch
            {
                ChallengeCloseReason.Declined  => $"{name} declined.",
                ChallengeCloseReason.Cancelled => $"{name} withdrew the challenge.",
                ChallengeCloseReason.Expired   => "The challenge timed out.",
                ChallengeCloseReason.Left      => $"{name} went offline.",
                _                              => "That challenge is no longer open.",
            };
        }

        private static void Set(ChallengePhase phase, string code, string challengeId, int ttlMs = 0)
        {
            Phase = phase;
            Code = code;
            ChallengeId = challengeId;
            ExpiresAt = ttlMs > 0 ? Time.realtimeSinceStartup + ttlMs / 1000f : 0f;
            Notice = "";
            Raise();
        }

        /// <summary>End the challenge, disarm any deck it armed, and leave <paramref name="notice"/>.</summary>
        private static void Abort(string notice)
        {
            bool armed = Phase == ChallengePhase.Sending || Phase == ChallengePhase.WaitingForAnswer ||
                         Phase == ChallengePhase.Starting;
            if (armed) MatchDeckSelection.Take();
            if (IsPicking) MatchDeckSelection.Cancel();
            Clear();
            Notice = notice;
            Raise();
        }

        private static void Clear()
        {
            Phase = ChallengePhase.None;
            Code = "";
            ChallengeId = "";
            ExpiresAt = 0f;
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
