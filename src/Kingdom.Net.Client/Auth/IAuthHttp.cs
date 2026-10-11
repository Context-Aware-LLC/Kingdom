using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Client.Auth
{
    /// <summary>
    /// One HTTPS POST or PUT, as the smallest thing the auth calls actually need. The host supplies
    /// the implementation for the same reason <see cref="IWireTransport"/> exists: the core targets
    /// netstandard2.1 and is consumed by Unity, where <c>UnityWebRequest</c> is the transport that
    /// works on every platform we ship to — but the logic above it has to be testable in the dotnet
    /// suite, which is the only place with a CI gate.
    ///
    /// <para><b>Almost every Supabase auth call we make is a POST</b> — send-link, exchange, refresh.
    /// The one exception is <c>PUT /auth/v1/user</c> (NOR-246, setting a password on an already
    /// signed-in session), which is the one GoTrue endpoint that is update semantics rather than
    /// create — hence <see cref="PutJsonAsync"/>. The OAuth authorize step is a URL the player's
    /// browser opens, not a request we issue, so no Supabase call needs a GET.</para>

    /// <para><b><see cref="GetAsync"/> and <see cref="DeleteAsync"/> are not for Supabase at all</b>
    /// (2026-09-01, card-ownership slice 3; the delete 2026-09-02, roster persistence). They reach
    /// Kingdom's own match server — <c>GET /collection</c>, <c>GET /roster</c>, and the two delete
    /// routes — which is bearer-authed with the same Supabase JWT and therefore wants the same
    /// transport: one implementation per platform, not two. The seam is named for its first caller
    /// rather than its only one; the contract below ("never throw") is what actually defines it.</para>
    ///
    /// <para><b>Never throw.</b> Report a failure to reach the server as
    /// <see cref="AuthHttpResponse.TransportFailure"/>. A thrown exception on the connect path would
    /// turn "the network is bad" into "the game did not start".</para>
    /// </summary>
    public interface IAuthHttp
    {
        Task<AuthHttpResponse> PostJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default);

        Task<AuthHttpResponse> PutJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default);

        /// <summary>A GET with headers and no body. Same never-throw contract as the others.</summary>
        Task<AuthHttpResponse> GetAsync(
            string url,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// A DELETE with headers and no body, for <c>DELETE /decks/{id}</c> and
        /// <c>DELETE /characters/{id}</c>. Same never-throw contract as the others.
        ///
        /// <para>Also not a Supabase call — see <see cref="GetAsync"/>. The id being deleted lives
        /// in the URL, never in a body, so there is nothing to serialise.</para>
        /// </summary>
        Task<AuthHttpResponse> DeleteAsync(
            string url,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// What came back. <see cref="StatusCode"/> 0 means nothing did — see
    /// <see cref="TransportFailure"/>, which is the case the caller must treat as transient rather
    /// than as a dead credential.
    /// </summary>
    public readonly struct AuthHttpResponse
    {
        /// <summary>HTTP status, or 0 when the request never completed.</summary>
        public int StatusCode { get; }

        /// <summary>Response body, or the transport's own error text when <see cref="StatusCode"/>
        /// is 0. Empty is legal.</summary>
        public string Body { get; }

        private AuthHttpResponse(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body ?? "";
        }

        public static AuthHttpResponse Completed(int statusCode, string body)
            => new AuthHttpResponse(statusCode, body);

        /// <summary>The request did not complete: no DNS, no route, timeout, cancelled socket.</summary>
        public static AuthHttpResponse TransportFailure(string detail)
            => new AuthHttpResponse(0, detail);

        public bool IsTransportFailure => StatusCode == 0;

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    }
}
