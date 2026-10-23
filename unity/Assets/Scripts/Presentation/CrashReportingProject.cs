namespace Kingdom.Presentation
{
    /// <summary>
    /// WHERE THE SENTRY PROJECT GOES. Paste the DSN, the organisation slug and the project slug here;
    /// nothing else in the repo names them.
    ///
    /// <para><b>None of these is a secret.</b> A DSN is public by design: it ships inside every build
    /// and only lets a client SEND events. The secret is the auth token that uploads debug symbols, and
    /// it is never written here or anywhere in the repo — the build reads it from the
    /// <c>SENTRY_AUTH_TOKEN</c> environment variable (<c>Kingdom.Editor.KingdomSentryCliOptions</c>).</para>
    ///
    /// <para><b>An empty DSN turns crash reporting off, silently.</b> The SDK stays disabled, logs
    /// nothing, tags nothing, and the Android build strips its native layer
    /// (<see cref="KingdomSentryOptions"/>). Symbol upload also needs <see cref="Project"/> and
    /// <see cref="Organization"/>; without them it is skipped, never failed.</para>
    /// </summary>
    public static class CrashReportingProject
    {
        /// <summary>The Sentry project's client key (Settings → Client Keys). Empty = disabled.</summary>
        public const string Dsn = "";

        /// <summary>The organisation slug, as in <c>https://sentry.io/organizations/&lt;slug&gt;/</c>.</summary>
        public const string Organization = "";

        /// <summary>The project slug, as in the project's settings URL.</summary>
        public const string Project = "";
    }
}
