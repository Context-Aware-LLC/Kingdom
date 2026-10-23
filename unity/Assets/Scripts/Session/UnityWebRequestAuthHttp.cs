using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using UnityEngine.Networking;

namespace Kingdom.Session
{
    /// <summary>
    /// <see cref="IAuthHttp"/> over <c>UnityWebRequest</c>.
    ///
    /// <para><b>Why not <c>HttpClient</c>.</b> It is the obvious choice and it is the wrong one here:
    /// on WebGL there are no sockets, and on Android/iOS the platform TLS stack is reached through
    /// <c>UnityWebRequest</c>. One transport that works everywhere beats a per-platform branch behind
    /// the game's sign-in.</para>
    ///
    /// <para><b>Call this from the main thread.</b> <c>UnityWebRequest.SendWebRequest</c> is a Unity
    /// API and its completion callback is delivered on the main thread; the returned Task therefore
    /// resumes there too. The connect path already awaits it from a coroutine.</para>
    ///
    /// <para><b>Never throws.</b> A protocol or connection error becomes
    /// <see cref="AuthHttpResponse.TransportFailure"/>, which the layer above treats as transient
    /// rather than as a dead credential.</para>
    /// </summary>
    public sealed class UnityWebRequestAuthHttp : IAuthHttp
    {
        private readonly int _timeoutSeconds;

        /// <summary>Raised on the main thread after any request through any instance completes with a
        /// 2xx. The first-session funnel listens, because a request that just succeeded is the cheapest
        /// moment to flush rows an unreachable boot left queued. A listener that throws is ignored.</summary>
        public static event Action Answered;

        public UnityWebRequestAuthHttp(int timeoutSeconds = 20)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        public Task<AuthHttpResponse> PostJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
            => SendJsonAsync(UnityWebRequest.kHttpVerbPOST, url, jsonBody, headers, cancellationToken);

        public Task<AuthHttpResponse> PutJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
            => SendJsonAsync(UnityWebRequest.kHttpVerbPUT, url, jsonBody, headers, cancellationToken);

        /// <summary>Non-Supabase: <c>GET /collection</c> and <c>GET /roster</c> off Kingdom's own
        /// server. A null body is what distinguishes it — <c>UnityWebRequest</c> will happily attach
        /// an upload handler to a GET, and some proxies drop the request when it does.</summary>
        public Task<AuthHttpResponse> GetAsync(
            string url,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
            => SendJsonAsync(UnityWebRequest.kHttpVerbGET, url, null, headers, cancellationToken);

        /// <summary>Non-Supabase: <c>DELETE /decks/{id}</c> and <c>DELETE /characters/{id}</c>. Body
        /// is null for the same reason as <see cref="GetAsync"/> — the id is in the URL.</summary>
        public Task<AuthHttpResponse> DeleteAsync(
            string url,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
            => SendJsonAsync(UnityWebRequest.kHttpVerbDELETE, url, null, headers, cancellationToken);

        private Task<AuthHttpResponse> SendJsonAsync(
            string verb,
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<AuthHttpResponse>();

            UnityWebRequest request;
            try
            {
                request = new UnityWebRequest(url, verb)
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = _timeoutSeconds,
                };

                // A null body means GET. Attaching an UploadHandlerRaw anyway sends a
                // Content-Length on a request that has no entity, which some proxies reject
                // outright — so the handler is omitted rather than given an empty buffer.
                if (jsonBody != null)
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));

                if (headers != null)
                    foreach (var header in headers)
                        request.SetRequestHeader(header.Key, header.Value);
            }
            catch (Exception ex)
            {
                completion.SetResult(AuthHttpResponse.TransportFailure(ex.Message));
                return completion.Task;
            }

            CancellationTokenRegistration registration = default;
            if (cancellationToken.CanBeCanceled)
                registration = cancellationToken.Register(() => { try { request.Abort(); } catch { } });

            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                try
                {
                    // A 4xx is ProtocolError, not a failure to reach Supabase — the status and the
                    // body are exactly what the layer above needs to tell "revoked" from "offline",
                    // so it must not be collapsed into a transport failure.
                    if (request.result == UnityWebRequest.Result.ConnectionError ||
                        request.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        completion.SetResult(AuthHttpResponse.TransportFailure(request.error ?? "the request did not complete"));
                        return;
                    }

                    var response = AuthHttpResponse.Completed(
                        (int)request.responseCode,
                        request.downloadHandler != null ? request.downloadHandler.text : "");
                    completion.SetResult(response);
                    if (response.IsSuccess) RaiseAnswered();
                }
                catch (Exception ex)
                {
                    completion.SetResult(AuthHttpResponse.TransportFailure(ex.Message));
                }
                finally
                {
                    registration.Dispose();
                    request.Dispose();
                }
            };

            return completion.Task;
        }

        private static void RaiseAnswered()
        {
            try { Answered?.Invoke(); }
            catch (Exception) { /* a listener must not turn a success into a transport failure */ }
        }
    }
}
