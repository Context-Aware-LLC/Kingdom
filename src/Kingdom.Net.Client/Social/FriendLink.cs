using System;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Client.Social
{
    /// <summary>
    /// A friend code as a link (#424): <c>https://kingdomofbadideas.com/f/XXXXX-XXXXX</c>, which the
    /// Share button sends and the site's <c>/f/</c> page answers, and <c>emberfall://friend/XXXXXXXXXX</c>,
    /// which that page's "Open in the game" button opens. Either one fills the friends drawer's
    /// add field; nothing is sent until the player taps Add.
    ///
    /// <para><b>The link carries the code and nothing else.</b> The page cannot name the player behind
    /// it: a route that turned a code into a name would let anyone test codes for owners, which is
    /// the enumeration <c>/friends/request</c> is built to refuse.</para>
    /// </summary>
    public static class FriendLink
    {
        public const string SiteHost = "kingdomofbadideas.com";
        public const string AppScheme = "emberfall";
        public const string AppHost = "friend";

        private const string SitePath = "/f/";

        /// <summary>The link the Share button sends. Expects a canonical code.</summary>
        public static string For(string code) => "https://" + SiteHost + SitePath + FriendCode.Display(code);

        /// <summary>
        /// The canonical code a link carries. False for anything that is not one of the two forms
        /// above, or whose code fails <see cref="FriendCode.TryNormalize"/> — so an auth redirect, a
        /// mistyped link and a stranger's URL all leave the add field alone.
        /// </summary>
        public static bool TryRead(string? url, out string code)
        {
            code = "";
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

            string path = uri.AbsolutePath;
            string raw;
            if (uri.Scheme == Uri.UriSchemeHttps && IsSite(uri.Host) &&
                path.StartsWith(SitePath, StringComparison.Ordinal))
                raw = path.Substring(SitePath.Length);
            else if (string.Equals(uri.Scheme, AppScheme, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase))
                raw = path.TrimStart('/');
            else
                return false;

            return FriendCode.TryNormalize(Uri.UnescapeDataString(raw.TrimEnd('/')), out code);
        }

        private static bool IsSite(string host)
            => string.Equals(host, SiteHost, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "www." + SiteHost, StringComparison.OrdinalIgnoreCase);
    }
}
