using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;
using UnityEngine.Networking;

namespace Emberfall.Session
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
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody ?? "{}")),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = _timeoutSeconds,
                };

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

                    completion.SetResult(AuthHttpResponse.Completed(
                        (int)request.responseCode,
                        request.downloadHandler != null ? request.downloadHandler.text : ""));
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
    }
}
