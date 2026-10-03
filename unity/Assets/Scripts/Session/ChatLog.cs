using System;
using System.Collections.Generic;
using Emberfall.Net.Protocol;

namespace Emberfall.Session
{
    /// <summary>
    /// This session's direct messages, per friend (#409). <b>Memory only</b>: chat is ephemeral by
    /// Sam's ruling, the server keeps nothing, and neither does this — it dies with the app or an
    /// account switch, and is never written to disk.
    ///
    /// <para>A line this player sends is shown at once as <see cref="LineState.Pending"/>, and
    /// settles when the server answers: its echo replaces the text with the server-cleaned copy the
    /// friend actually saw, a refusal marks it <see cref="LineState.Failed"/>. The server handles
    /// one socket's frames in order, so answers settle the oldest pending line to that friend.</para>
    ///
    /// <para><b>Every <see cref="Line.Text"/> from a friend is text another player typed</b>
    /// (VULN-004): whatever draws it owes rich text off and a bounded box.</para>
    ///
    /// <para>Fed by <see cref="SocialGateway"/> on the main thread; read by the friends drawer.</para>
    /// </summary>
    public static class ChatLog
    {
        /// <summary>Older lines fall off; a long session is not a reason to grow without bound.</summary>
        public const int MaxPerFriend = 100;

        public enum LineState { Pending, Delivered, Failed }

        public readonly struct Line
        {
            public readonly string Text;
            public readonly bool Mine;
            public readonly LineState State;

            public Line(string text, bool mine, LineState state)
            {
                Text = text;
                Mine = mine;
                State = state;
            }
        }

        private static readonly Dictionary<string, List<Line>> Lines = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> UnreadBy = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>A conversation changed; the argument is the friend's code. Main thread.</summary>
        public static event Action<string> Changed;

        private static readonly IReadOnlyList<Line> Empty = Array.Empty<Line>();

        public static IReadOnlyList<Line> For(string code) =>
            Lines.TryGetValue(code, out List<Line> lines) ? lines : Empty;

        public static int Unread(string code) => UnreadBy.TryGetValue(code, out int n) ? n : 0;

        public static int UnreadTotal
        {
            get
            {
                int total = 0;
                foreach (int n in UnreadBy.Values) total += n;
                return total;
            }
        }

        public static void MarkRead(string code)
        {
            if (UnreadBy.Remove(code)) Raise(code);
        }

        /// <summary>A line this player is about to send, shown before the server answers.</summary>
        public static void AddPending(string code, string text) => Append(code, new Line(text, true, LineState.Pending));

        /// <summary>The send never left this device.</summary>
        public static void FailOldestPending(string code) => Settle(code, null);

        /// <summary>Everything the social connection hands over that concerns chat.</summary>
        internal static void OnSocial(SocialMsg msg)
        {
            switch (msg)
            {
                case ChatMsg chat when chat.Mine:
                    Settle(chat.Code, chat.Text);
                    break;
                case ChatMsg chat:
                    UnreadBy[chat.Code] = Unread(chat.Code) + 1;
                    Append(chat.Code, new Line(chat.Text, false, LineState.Delivered));
                    break;
                case ChatRefusedMsg refused:
                    Settle(refused.Code, null);
                    break;
            }
        }

        /// <summary>The connection dropped: nothing still pending will be answered.</summary>
        public static void FailAllPending()
        {
            foreach (KeyValuePair<string, List<Line>> conversation in Lines)
            {
                List<Line> lines = conversation.Value;
                bool changed = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].State != LineState.Pending) continue;
                    lines[i] = new Line(lines[i].Text, true, LineState.Failed);
                    changed = true;
                }
                if (changed) Raise(conversation.Key);
            }
        }

        public static void Clear()
        {
            Lines.Clear();
            UnreadBy.Clear();
            Raise("");
        }

        /// <summary>Settle the oldest pending line: delivered with the server's text, or failed.</summary>
        private static void Settle(string code, string deliveredText)
        {
            if (!Lines.TryGetValue(code, out List<Line> lines)) return;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].State != LineState.Pending) continue;
                lines[i] = deliveredText != null
                    ? new Line(deliveredText, true, LineState.Delivered)
                    : new Line(lines[i].Text, true, LineState.Failed);
                Raise(code);
                return;
            }
        }

        private static void Append(string code, Line line)
        {
            if (!Lines.TryGetValue(code, out List<Line> lines))
                Lines[code] = lines = new List<Line>();
            lines.Add(line);
            if (lines.Count > MaxPerFriend) lines.RemoveAt(0);
            Raise(code);
        }

        private static void Raise(string code)
        {
            try { Changed?.Invoke(code); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }
    }
}
