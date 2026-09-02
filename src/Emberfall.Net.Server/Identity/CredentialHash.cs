using System;
using System.Security.Cryptography;
using System.Text;

namespace Emberfall.Net.Server.Identity
{
    /// <summary>
    /// 16 hex bytes (128 bits) of SHA-256 — the one spelling of "hash a credential" on this server.
    ///
    /// <para><b>It is shared rather than duplicated because a second spelling is not a style problem,
    /// it is an account split.</b> This hash is what a Supabase subject becomes before
    /// <c>IAccountStore.ResolveOrCreateAsync</c> looks it up, so two call sites that hash differently
    /// resolve the same human to two different <c>player_id</c>s — one holding their collection, one
    /// empty — with nothing anywhere reporting a fault. <c>MatchGate</c> and
    /// <see cref="Emberfall.Net.Server.Accounts.CollectionEndpoint"/> both authenticate the same
    /// player, so they must both arrive here.</para>
    ///
    /// <para><b>Also the log-safe form of an email</b> (security.md §4.5 — never log a raw one).
    /// Same output width and same function; the distinction is only what the caller feeds it.</para>
    ///
    /// <para>Truncated to 128 bits deliberately: same shape as <c>AccountId</c>'s value, and this is a
    /// lookup key and a log token, never a password digest.</para>
    /// </summary>
    public static class CredentialHash
    {
        public static string Of(string value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(value), digest);
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
