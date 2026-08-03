using System;
using System.Globalization;
using System.Text;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// The player behind the roster — one per identity scope (accounts posture decision 6a: guest
    /// identity is "device token + display name", and this is the display-name half).
    ///
    /// <para><b>Deliberately thin, and deliberately not the identity itself.</b> The identity is the
    /// device token, which lives in <c>PlayerPrefs</c> on the Unity side and is the storage key this
    /// file is scoped by (<see cref="CollectionPaths.Profile"/>). It is <b>not</b> copied in here: it
    /// is a bearer identity — whoever holds it can claim these matches — and a second copy on disk is
    /// a second thing to leak, for no gain, since the folder name already is it. The token's own
    /// class owns its persistence; this file owns the one thing the player chose.</para>
    ///
    /// <para><b>No public player id here either.</b> The server derives one by hashing the token
    /// (<c>Emberfall.Net.Server.Identity.AccountId</c>) and that derivation is deliberately
    /// single-definition — a client-side copy of the same hash is a second definition free to drift
    /// on a truncation width or hex casing. If the client ever needs to show its own id, the server
    /// should tell it.</para>
    ///
    /// <para><b>No timestamps.</b> Same reason <c>DeckList</c> has none (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md
    /// §3): this assembly is deterministic and holds no clock. A "created at" would have to be handed
    /// in by the caller, and nothing reads it.</para>
    /// </summary>
    public sealed class PlayerProfile
    {
        /// <summary>
        /// What other players will see. Mutable — a name is a preference, not an identity, and
        /// renaming must never invalidate a match history keyed on the token.
        ///
        /// <para>Empty means <b>never set</b>, which is the first-run state and not an error: a boot
        /// that goes straight to a board scene has no opportunity to ask. Callers render a
        /// placeholder; they do not invent a name and save it, because a saved "Player" is
        /// indistinguishable from a chosen one and the prompt would never fire again.</para>
        ///
        /// <para>Validated on the way IN (<see cref="JsonProfileStore.Save"/> refuses an invalid
        /// name) and reported as stored on the way OUT — the same split as an invalid class pairing.
        /// See <see cref="PlayerName"/> for the rules.</para>
        /// </summary>
        public string DisplayName { get; set; }

        public PlayerProfile(string displayName = "")
        {
            DisplayName = displayName ?? "";
        }

        /// <summary>False on first run and after a name is cleared — the signal to ask for one.</summary>
        public bool HasDisplayName => !string.IsNullOrEmpty(DisplayName);
    }

    /// <summary>The verdict on a candidate display name: the cleaned-up value, or why it was refused.</summary>
    public readonly struct NameCheck
    {
        /// <summary>True iff <see cref="Normalized"/> is a name we will store.</summary>
        public readonly bool IsValid;

        /// <summary>
        /// The value to store — trimmed, whitespace-collapsed, and canonically composed. Empty when
        /// <see cref="IsValid"/> is false. <b>Store this, not the caller's input</b>, or two players
        /// who typed the same name end up with different bytes.
        /// </summary>
        public readonly string Normalized;

        /// <summary>
        /// Player-facing refusal, ready to render in the name field's error line. Empty when valid.
        /// Written as a sentence a player can act on ("Names are 3–16 characters long."), not as a
        /// rule id — this string is the entire feedback surface of the create form.
        /// </summary>
        public readonly string Reason;

        private NameCheck(bool valid, string normalized, string reason)
        {
            IsValid = valid;
            Normalized = normalized;
            Reason = reason;
        }

        internal static NameCheck Ok(string normalized) => new NameCheck(true, normalized, "");
        internal static NameCheck Bad(string reason) => new NameCheck(false, "", reason);
    }

    /// <summary>
    /// What counts as a display name (accounts posture decision 6a).
    ///
    /// <para><b>Why this is a validator and not a length check.</b> A display name is the first
    /// player-authored string in the project that is destined to be rendered on <i>somebody else's
    /// machine</i> — the versus-screen nameplate and the board plate opposite. That is the shape of
    /// VULN-001 (a client-authored string detonating on the peer's client), and the security index
    /// carries a row for it.</para>
    ///
    /// <para><b>This now runs on BOTH ends, and the receiving end is the one that counts</b>
    /// (EF-073, VULN-002 §4.1). <c>HelloMsg</c> carries a display name, so
    /// <c>Emberfall.Net.Server.Transport.MatchGate</c> calls this same <see cref="Check"/> on
    /// arrival before the name is ever put in an <c>OpponentInfo</c>. That is why this assembly is
    /// UnityEngine-free: the server needs the identical predicate, not a re-implementation free to
    /// drift from it. <b>A rule relaxed here is relaxed on the wire</b> — this type is no longer a
    /// local-only convenience.</para>
    ///
    /// <para><b>Normalize where we can, refuse where we must.</b> Leading/trailing and doubled
    /// whitespace are fixed silently — refusing a name over a stray space is hostile and teaches
    /// nothing. Control and format characters are refused outright: they are invisible, so a player
    /// cannot see what they pasted, and a bidi override (U+202E) or a zero-width joiner in a
    /// nameplate is a spoofing primitive rather than a name.</para>
    ///
    /// <para><b>What this deliberately does NOT solve: impersonation.</b> Unicode letters are allowed,
    /// so Cyrillic "Ѕаm" passes and looks like "Sam". Fixing that needs a uniqueness namespace to
    /// impersonate <i>within</i>, and guest identity has none — two devices may hold the same name
    /// today and neither is wrong. Confusable folding belongs with the account registry that first
    /// makes names unique, not here, where it would only give false assurance.</para>
    /// </summary>
    public static class PlayerName
    {
        /// <summary>Short enough to fit a nameplate, long enough not to feel like a handle limit.</summary>
        public const int MinLength = 3;

        /// <summary>
        /// The cap the nameplate is laid out against (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/versus-screen.md §6). A renderer must
        /// still clip or ellipsize — this bounds the string, not the pixels, and 16 wide glyphs are
        /// not 16 narrow ones.
        /// </summary>
        public const int MaxLength = 16;

        /// <summary>Punctuation a name may contain. Everything outside this and letters/digits/marks is refused.</summary>
        private const string AllowedPunctuation = " -_'.";

        /// <summary>
        /// Strings our own UI authors, which a player therefore may not claim (VULN-002 §4.3).
        ///
        /// <para><b>Every character in these is legal, which is the point.</b> The other rules ask
        /// "is this a name?"; this one asks "could a player mistake this for the game talking?" A
        /// nameplate reading <c>Opponent</c> or <c>SYSTEM</c> is an impersonation primitive built
        /// entirely out of allowed letters, so no character-class rule can reach it.</para>
        ///
        /// <para>Sourced from what the UI actually writes: <c>You</c> and <c>Opponent</c> are the
        /// versus-screen nameplate fallbacks, <c>Unnamed player</c> is the main menu's, and
        /// <c>AI Opponent</c> is the single-player one. <b>Add to this list in the same commit that
        /// teaches a screen a new placeholder</b>, or that placeholder is claimable the day it
        /// ships.</para>
        /// </summary>
        private static readonly string[] ReservedNames =
        {
            "You", "Opponent", "AI Opponent", "Unnamed player", "Emberfall", "SYSTEM",
        };

        /// <summary>
        /// True if <paramref name="candidate"/> collides with a string our own chrome writes.
        /// Compared case-insensitively on the NORMALIZED value — <c>sYsTeM</c> and
        /// <c>"  SYSTEM  "</c> impersonate exactly as well as the original.
        /// </summary>
        public static bool IsReserved(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            string normalized = Normalize(candidate);
            foreach (string reserved in ReservedNames)
                if (string.Equals(normalized, reserved, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Validate and clean a candidate name. Never throws — an unusable name is a
        /// <see cref="NameCheck"/> with a reason, because every caller is a text field that has to
        /// say something back.
        /// </summary>
        public static NameCheck Check(string raw)
        {
            if (raw == null) return NameCheck.Bad("Enter a name.");

            string normalized = Normalize(raw);
            if (normalized.Length == 0) return NameCheck.Bad("Enter a name.");

            // Length is measured on the normalized value, so "  Sam  " is three characters and not
            // seven — the player is judged on what gets stored, not on what they typed.
            if (normalized.Length < MinLength)
                return NameCheck.Bad($"Names are at least {MinLength} characters long.");
            if (normalized.Length > MaxLength)
                return NameCheck.Bad($"Names are at most {MaxLength} characters long.");

            bool hasSubstance = false;
            foreach (char c in normalized)
            {
                if (AllowedPunctuation.IndexOf(c) >= 0) continue;

                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                switch (cat)
                {
                    // Letters and digits are the name. Marks are allowed with them because in
                    // Devanagari, Thai and Hebrew the vowel signs ARE the word — refusing marks
                    // would refuse those scripts outright while looking like a safety rule.
                    case UnicodeCategory.UppercaseLetter:
                    case UnicodeCategory.LowercaseLetter:
                    case UnicodeCategory.TitlecaseLetter:
                    case UnicodeCategory.ModifierLetter:
                    case UnicodeCategory.OtherLetter:
                    case UnicodeCategory.DecimalDigitNumber:
                        hasSubstance = true;
                        continue;

                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.SpacingCombiningMark:
                    case UnicodeCategory.EnclosingMark:
                        continue;

                    // Invisible by definition: the player cannot see what they pasted, and a bidi
                    // override or zero-width joiner in a nameplate is a spoof, not a name. Named
                    // separately from the catch-all so the message does not quote a glyph that
                    // renders as nothing.
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                        return NameCheck.Bad("That name contains characters that can't be displayed.");

                    default:
                        return NameCheck.Bad($"'{c}' isn't allowed in a name.");
                }
            }

            // "..." and "---" pass every rule above and are not names. Punctuation is a garnish on
            // something, so require the something.
            if (!hasSubstance)
                return NameCheck.Bad("Names need at least one letter or number.");

            // Last, because it is the only rule about MEANING rather than characters — and the only
            // one whose refusal is not about the name being unusable (VULN-002 §4.3).
            if (IsReserved(normalized))
                return NameCheck.Bad($"'{normalized}' is reserved. Pick another name.");

            return NameCheck.Ok(normalized);
        }

        /// <summary>Convenience for callers that only branch on the verdict.</summary>
        public static bool IsValid(string raw) => Check(raw).IsValid;

        /// <summary>
        /// Trim, collapse internal whitespace runs to one space, and compose canonically.
        ///
        /// <para><b>The composition is best-effort on purpose.</b> <c>String.Normalize</c> has no
        /// guaranteed implementation on every IL2CPP target and has historically thrown
        /// <see cref="PlatformNotSupportedException"/> on some — a failure that could only ever
        /// appear in a player build, never in the Editor, which is exactly the class of defect this
        /// project has been bitten by before. A name that is merely un-composed is a working name, so
        /// the fallback is to keep the trimmed value rather than to refuse it.</para>
        /// </summary>
        private static string Normalize(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            bool pendingSpace = false;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c))
                {
                    // Only emitted once something non-space follows, which drops trailing runs for free.
                    if (sb.Length > 0) pendingSpace = true;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }

            string collapsed = sb.ToString();
            try
            {
                return collapsed.Normalize(NormalizationForm.FormC);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is ArgumentException)
            {
                // ArgumentException is what Normalize raises for invalid Unicode (a lone surrogate).
                // Those characters are refused by the category scan above anyway, so handing the
                // un-normalized value back loses nothing.
                return collapsed;
            }
        }
    }
}
