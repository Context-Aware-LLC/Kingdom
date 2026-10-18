using System;

namespace Kingdom.Net.Server
{
    /// <summary>
    /// Reads the server's environment variables, all named <c>KINGDOM_*</c>. They were
    /// <c>EMBERFALL_*</c> until 2026-10-18, after the codename became Kingdom.
    /// </summary>
    public static class KingdomEnv
    {
        private const string Prefix = "KINGDOM_";

        /// <summary>
        /// The variable's value, or null when it is unset or blank. Takes the full name,
        /// <c>Get("KINGDOM_ACCOUNTS_DB")</c>, and also answers the old <c>EMBERFALL_</c> name.
        /// </summary>
        public static string? Get(string name)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                throw new ArgumentException($"'{name}' is not a {Prefix} variable.", nameof(name));

            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;

            // RETIREMENT TRIGGER: the EMBERFALL_ fallback exists only so a deploy or a dev machine
            // still holding the old names keeps working. Delete it once `fly secrets list -a <app>`
            // shows no EMBERFALL_ name on any app and `Get-ChildItem Env:EMBERFALL_*` is empty on
            // Sam's machine, together with the other fallbacks:
            // `git grep -n "EMBERFALL_" -- src tooling tools tests unity`.
            value = Environment.GetEnvironmentVariable("EMBERFALL_" + name.Substring(Prefix.Length));
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
