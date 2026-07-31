using System;
using System.IO;
using System.Text;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The canonical accounts + entitlement DDL, embedded so both Postgres stores apply EXACTLY this
    /// text on startup and there is no second copy to drift (mirrors <c>TelemetryDrain</c>'s embedded
    /// <c>schema.sql</c>). Loaded once.
    /// </summary>
    internal static class AccountsSchema
    {
        private const string Resource = "Emberfall.Net.Server.Accounts.schema.sql";

        public static string Ddl { get; } = Load();

        private static string Load()
        {
            var asm = typeof(AccountsSchema).Assembly;
            using var s = asm.GetManifestResourceStream(Resource)
                ?? throw new InvalidOperationException($"Embedded schema '{Resource}' not found.");
            using var reader = new StreamReader(s, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
