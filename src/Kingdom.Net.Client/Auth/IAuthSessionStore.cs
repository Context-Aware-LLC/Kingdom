using System;

namespace Kingdom.Net.Client.Auth
{
    /// <summary>
    /// Where the signed-in session survives a restart. The host app supplies this — the core has no
    /// filesystem and no <c>PlayerPrefs</c>.
    ///
    /// <para><b>Never throw.</b> Unreadable storage must degrade to guest, not fail a boot. A player
    /// whose token file is corrupt should be able to start the game and sign in again; an exception
    /// here would instead take down the launch path for a value nothing requires.</para>
    ///
    /// <para><b>This holds a bearer credential.</b> Anything with read access to it can play as that
    /// account until the token is revoked. Do not log its contents, and do not put it anywhere the
    /// device shares between accounts.</para>
    /// </summary>
    public interface IAuthSessionStore
    {
        /// <summary>The stored session, or <see cref="AuthSession.None"/> when there is none or it
        /// cannot be read.</summary>
        AuthSession Load();

        /// <summary>Persist immediately. A refresh that dies with the process would leave the next
        /// launch presenting a token the server has already stopped accepting.</summary>
        void Save(AuthSession session);

        /// <summary>Forget the session — sign-out, and a refresh Supabase rejected outright.</summary>
        void Clear();

        /// <summary>
        /// The sign-in currently waiting to be finished, or <see cref="PendingSignIn.None"/>.
        ///
        /// <para><b>Why this is persisted and not just held in memory.</b> A magic link is opened in a
        /// mail client, and on mobile that hands control to the browser and back — the game process
        /// that sent the mail is frequently not the one the code returns to. An in-memory verifier
        /// would therefore fail exactly the flow this exists for, and fail it only on the platform we
        /// ship first.</para>
        /// </summary>
        PendingSignIn LoadPending();

        void SavePending(PendingSignIn pending);

        /// <summary>Drop the pending sign-in once it is redeemed or abandoned. A verifier kept past
        /// its code is a secret with no purpose.</summary>
        void ClearPending();
    }

    /// <summary>Process-lifetime store. The tests' store, and the honest default for a host that has
    /// not wired persistence yet: sign-in works, it just does not survive a restart.</summary>
    public sealed class InMemoryAuthSessionStore : IAuthSessionStore
    {
        private AuthSession _session = AuthSession.None;
        private PendingSignIn _pending = PendingSignIn.None;

        public InMemoryAuthSessionStore() { }

        public InMemoryAuthSessionStore(AuthSession seed)
        {
            _session = seed ?? AuthSession.None;
        }

        public AuthSession Load() => _session;

        public void Save(AuthSession session) => _session = session ?? AuthSession.None;

        public void Clear() => _session = AuthSession.None;

        public PendingSignIn LoadPending() => _pending;

        public void SavePending(PendingSignIn pending) => _pending = pending ?? PendingSignIn.None;

        public void ClearPending() => _pending = PendingSignIn.None;
    }
}
