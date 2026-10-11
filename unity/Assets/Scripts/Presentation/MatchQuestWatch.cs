using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Net.Client.Quests;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Finds out which daily quests one match moved, for the result screen (Sam, 2026-10-03). It
    /// reads <c>GET /quests</c> when the match is wired and again when it ends, and reports the
    /// difference — the server sends no per-match quest message, so two reads are the only way
    /// the client can know.
    ///
    /// <para><b>The second read retries.</b> The server applies quest progress on a queue behind
    /// the match (<c>QuestProgressSink</c>), so a read made the moment the match ends can still
    /// answer the old numbers. A match that moved nothing is indistinguishable from one not yet
    /// applied, so that case spends the whole retry budget — which is why the budget has to fit
    /// inside <see cref="MatchEndSequence.StingerSeconds"/>, where nobody is waiting on it.</para>
    ///
    /// <para>Every failure answers "nothing advanced". This is decoration on the result screen
    /// and must never be able to hold it up or put an error on it.</para>
    /// </summary>
    public sealed class MatchQuestWatch
    {
        private const int TimeoutSeconds = 10;
        private const int Attempts = 4;
        private const int RetryDelayMs = 1000;

        private static readonly IReadOnlyList<QuestAdvance> None = Array.Empty<QuestAdvance>();

        private readonly HttpQuestGateway _gateway = new HttpQuestGateway(
            CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds));

        private Task<QuestSnapshot> _before;
        private bool _cancelled;

        /// <summary>Take the baseline. Call once, when the match is wired.</summary>
        public void Begin() => _before = FetchAsync();

        /// <summary>Stop retrying — the scene that wanted the answer is gone.</summary>
        public void Cancel() => _cancelled = true;

        /// <summary>The quests this match advanced, or none. Never throws.</summary>
        public async Task<IReadOnlyList<QuestAdvance>> AdvancedAsync()
        {
            if (_before == null) return None;
            QuestSnapshot before = await _before;
            if (before.Status != QuestFetchStatus.Known) return None;

            for (int attempt = 0; attempt < Attempts && !_cancelled; attempt++)
            {
                if (attempt > 0) await Task.Delay(RetryDelayMs);
                if (_cancelled) break;
                IReadOnlyList<QuestAdvance> advanced =
                    QuestProgressDiff.Advanced(before, await FetchAsync());
                if (advanced.Count > 0) return advanced;
            }
            return None;
        }

        private async Task<QuestSnapshot> FetchAsync()
        {
            try
            {
                if (!AuthGateway.IsSignedIn)
                    return QuestSnapshot.Failed(QuestFetchStatus.Unauthenticated, "Not signed in.");
                string accessToken = await AuthGateway.TokenForConnectAsync();
                return await _gateway.FetchAsync(accessToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quests] match-end fetch threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "the result screen shows no quest progress.");
                return QuestSnapshot.Failed(QuestFetchStatus.Offline, ex.Message);
            }
        }
    }
}
