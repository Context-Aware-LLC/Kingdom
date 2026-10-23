using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Telemetry
{
    /// <summary>What one <see cref="FunnelUploader.FlushAsync"/> did.</summary>
    public enum FunnelFlushResult
    {
        /// <summary>Nothing was queued.</summary>
        Empty,
        /// <summary>Every queued row was taken by the server.</summary>
        Sent,
        /// <summary>A flush was already running; this one did nothing.</summary>
        Busy,
        /// <summary>A 429, a 5xx, an absent endpoint or no network: the rows stay queued and the
        /// next flush waits out a backoff.</summary>
        Kept,
        /// <summary>The server refused a batch (a 4xx other than those): it was removed, never retried.</summary>
        Dropped,
        /// <summary>No server is configured; nothing was sent.</summary>
        NotConfigured,
    }

    /// <summary>
    /// Posts the funnel queue to <c>POST /telemetry/client</c> as <c>{"rows":[...]}</c>.
    ///
    /// <para><b>Identity.</b> <see cref="FunnelHeaders.Device"/> always, and the bearer as well
    /// whenever this device holds a session. Both, not one or the other: the <c>sign_in</c> row is
    /// what binds the device to the account (design doc, item 2, Identity), and a row that carried
    /// only the bearer could bind nothing. A bearer the server refuses (401) is retried once with the
    /// device alone, so a token that expired under a queued row does not cost the row.</para>
    ///
    /// <para><b>What each answer does to the batch.</b> 2xx removes it. 429, 5xx, 404/405 (no such
    /// endpoint on this server yet — says nothing about the rows) and a transport failure keep it
    /// and back off. Any other 4xx removes it unsent: the server read the rows and refused them, and
    /// asking again cannot change that.</para>
    ///
    /// <para><b>Scheduling.</b> Nothing here owns a timer. The host calls <see cref="Tick"/> from its
    /// frame loop; a flush starts when one is due and none is running. <see cref="RequestFlush"/>
    /// (after an enqueue) is debounced so a burst of rows goes as one request;
    /// <see cref="RequestFlushNow"/> is for launch. After a kept batch the next automatic flush waits
    /// out a doubling backoff, so a rate-limited device does not hammer the limit it is under.</para>
    ///
    /// <para><b>Never throws, never ConfigureAwait(false).</b> Each request must start on the
    /// caller's context: in Unity that is the main thread, the only one <c>UnityWebRequest</c> may be
    /// created on (the same rule <c>UpdateGateRetry</c> states).</para>
    /// </summary>
    public sealed class FunnelUploader
    {
        public const string Route = "/telemetry/client";
        public const int DefaultBatchSize = 25;

        /// <summary>The server's per-request limits; more is a 413, which drops the batch.</summary>
        public const int MaxRowsPerRequest = 40;
        public const int MaxBodyBytes = 32 * 1024;

        public const long DebounceMs = 2_000;

        /// <summary>The server's 429 carries <c>Retry-After: 10</c>. <see cref="IAuthHttp"/> carries
        /// no response headers, so it is honoured as a floor: no backoff is ever shorter.
        /// RETIREMENT TRIGGER: if the server's Retry-After grows past <see cref="FirstBackoffMs"/>,
        /// raise FirstBackoffMs or give IAuthHttp a way to read the header.</summary>
        public const long RetryAfterFloorMs = 10_000;
        public const long FirstBackoffMs = 15_000;
        public const long MaxBackoffMs = 300_000;

        private readonly FunnelQueue _queue;
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;
        private readonly string _deviceToken;
        private readonly Func<Task<string>> _bearer;
        private readonly IClientClock _clock;
        private readonly Action<string>? _log;
        private readonly int _batchSize;

        private bool _inFlight;
        private long _dueAtMs = long.MaxValue;
        private long _backoffUntilMs = long.MinValue;
        private long _backoffMs;

        /// <param name="bearer">The access token to send, or empty when there is no session. Called
        /// once per flush. May throw or fault; that is treated as no session.</param>
        public FunnelUploader(
            FunnelQueue queue,
            string baseUrl,
            IAuthHttp http,
            string deviceToken,
            Func<Task<string>> bearer,
            IClientClock clock,
            Action<string>? log = null,
            int batchSize = DefaultBatchSize)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _deviceToken = deviceToken ?? "";
            _bearer = bearer ?? (() => Task.FromResult(""));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log;
            _batchSize = Math.Max(1, Math.Min(batchSize, MaxRowsPerRequest));
        }

        public bool IsFlushing => _inFlight;

        /// <summary>True while a kept batch's backoff runs; automatic flushes wait it out.</summary>
        public bool IsBackingOff => _clock.NowMs < _backoffUntilMs;

        /// <summary>Flush soon: after <see cref="DebounceMs"/>, or sooner if one is already due.</summary>
        public void RequestFlush() => Schedule(_clock.NowMs + DebounceMs);

        /// <summary>Flush on the next <see cref="Tick"/>.</summary>
        public void RequestFlushNow() => Schedule(_clock.NowMs);

        /// <summary>
        /// Some other request just succeeded, so the network is up: worth a flush if anything is
        /// waiting. Respects a running backoff — a 429 is about this device, not about the network.
        /// </summary>
        public void NotifyServerAnswered()
        {
            if (_queue.Count == 0 || IsBackingOff) return;
            RequestFlush();
        }

        /// <summary>Start a flush if one is due. Returns the flush it started, or null. Never throws.</summary>
        public Task<FunnelFlushResult>? Tick()
        {
            long now = _clock.NowMs;
            if (_inFlight || now < _dueAtMs || now < _backoffUntilMs) return null;
            _dueAtMs = long.MaxValue;
            if (_queue.Count == 0) return null;
            return FlushAsync();
        }

        /// <summary>Send everything queued, batch by batch, stopping at the first batch that is not
        /// taken. Never throws.</summary>
        public async Task<FunnelFlushResult> FlushAsync()
        {
            if (_inFlight) return FunnelFlushResult.Busy;
            if (_baseUrl.Length == 0) return FunnelFlushResult.NotConfigured;
            if (_queue.Count == 0) return FunnelFlushResult.Empty;

            _inFlight = true;
            try
            {
                string bearer = await BearerAsync();
                while (true)
                {
                    IReadOnlyList<FunnelQueue.Entry> batch = FitToBodyLimit(_queue.Peek(_batchSize));
                    if (batch.Count == 0)
                    {
                        _backoffMs = 0;
                        _backoffUntilMs = long.MinValue;
                        return FunnelFlushResult.Sent;
                    }

                    string body = Body(batch);
                    AuthHttpResponse response = await PostAsync(body, bearer);
                    if (response.StatusCode == 401 && bearer.Length > 0)
                    {
                        // The token is the problem, not the rows: send them on the device alone, and
                        // stop offering the token for the rest of this flush.
                        bearer = "";
                        response = await PostAsync(body, bearer);
                    }

                    if (response.IsSuccess)
                    {
                        // {status, accepted, dropped}: a row the server dropped was malformed and
                        // would be dropped again, so the whole batch leaves either way.
                        _queue.Remove(batch);
                        if (AuthJson.TryReadTopLevel(response.Body, out var fields) &&
                            AuthJson.TryInt64(fields, "dropped", out long dropped) && dropped > 0)
                            Log("server dropped " + dropped.ToString(CultureInfo.InvariantCulture) +
                                " of " + batch.Count + " row(s) as malformed.");
                        continue;
                    }

                    if (IsTransient(response))
                    {
                        BackOff();
                        Log("kept " + batch.Count + " row(s): " + Describe(response) + "; next try in " +
                            (_backoffMs / 1000).ToString(CultureInfo.InvariantCulture) + "s.");
                        return FunnelFlushResult.Kept;
                    }

                    _queue.Remove(batch);
                    Log("server refused " + batch.Count + " row(s) (" + Describe(response) + "); dropped them.");
                    return FunnelFlushResult.Dropped;
                }
            }
            catch (Exception ex)
            {
                // IAuthHttp is written not to throw; if it does, the rows are still queued.
                BackOff();
                Log("flush threw (" + ex.GetType().Name + ": " + ex.Message + "); rows kept.");
                return FunnelFlushResult.Kept;
            }
            finally
            {
                _inFlight = false;
            }
        }

        /// <summary>429 (rate-limited: the contract keeps the row queued), 5xx, 404/405 (this server
        /// has no such route yet), 408, and no answer at all.</summary>
        private static bool IsTransient(AuthHttpResponse response)
        {
            int s = response.StatusCode;
            return response.IsTransportFailure || s == 429 || s == 408 || s == 404 || s == 405 || s >= 500;
        }

        private void Schedule(long atMs)
        {
            if (atMs < _dueAtMs) _dueAtMs = atMs;
        }

        private void BackOff()
        {
            _backoffMs = _backoffMs == 0 ? FirstBackoffMs : Math.Min(_backoffMs * 2, MaxBackoffMs);
            _backoffUntilMs = _clock.NowMs + _backoffMs;
            // The rows are still queued, so the next automatic flush is the one after the backoff.
            Schedule(_backoffUntilMs);
        }

        private async Task<string> BearerAsync()
        {
            try { return (await _bearer()) ?? ""; }
            catch (Exception) { return ""; }
        }

        private Task<AuthHttpResponse> PostAsync(string body, string bearer)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Content-Type", "application/json"),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            if (_deviceToken.Length > 0)
                headers.Add(new KeyValuePair<string, string>(FunnelHeaders.Device, _deviceToken));
            if (bearer.Length > 0)
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + bearer));
            return _http.PostJsonAsync(_baseUrl + Route, body, headers);
        }

        /// <summary>The longest prefix of <paramref name="rows"/> whose body fits
        /// <see cref="MaxBodyBytes"/>, and never fewer than one row: a single row over the limit
        /// goes alone, the server answers 413, and it is dropped rather than blocking the queue.</summary>
        private static IReadOnlyList<FunnelQueue.Entry> FitToBodyLimit(IReadOnlyList<FunnelQueue.Entry> rows)
        {
            int bytes = Encoding.UTF8.GetByteCount("{\"rows\":[]}");
            for (int i = 0; i < rows.Count; i++)
            {
                bytes += Encoding.UTF8.GetByteCount(rows[i].Json) + (i > 0 ? 1 : 0);
                if (bytes > MaxBodyBytes && i > 0)
                {
                    var fitted = new List<FunnelQueue.Entry>(i);
                    for (int k = 0; k < i; k++) fitted.Add(rows[k]);
                    return fitted;
                }
            }
            return rows;
        }

        private static string Body(IReadOnlyList<FunnelQueue.Entry> batch)
        {
            var sb = new StringBuilder(64 + batch.Count * 200);
            sb.Append("{\"rows\":[");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(batch[i].Json);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Describe(AuthHttpResponse response)
            => response.IsTransportFailure
                ? "no answer"
                : "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);

        private void Log(string message)
        {
            try { _log?.Invoke("[Funnel] " + message); }
            catch { }
        }
    }
}
