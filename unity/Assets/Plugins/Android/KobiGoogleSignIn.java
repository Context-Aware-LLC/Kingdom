package com.contextaware.kobi.auth;

import android.app.Activity;
import android.os.CancellationSignal;

import androidx.annotation.Keep;
import androidx.credentials.Credential;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.CustomCredential;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.exceptions.GetCredentialCancellationException;
import androidx.credentials.exceptions.GetCredentialException;

import com.google.android.libraries.identity.googleid.GetGoogleIdOption;
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Android's own Google sign-in sheet (Credential Manager) for Unity: one call in, one callback out
 * with a Google ID token. NativeGoogleSignIn.cs calls this class and implements Callback by name,
 * which R8 cannot see, so both are @Keep.
 */
@Keep
public final class KobiGoogleSignIn {

    /** Implemented on the C# side by an AndroidJavaProxy. */
    @Keep
    public interface Callback {
        void onToken(String idToken);

        /** kind is "cancelled" when the player closed the sheet and "failed" for anything else. */
        void onError(String kind, String detail);
    }

    private KobiGoogleSignIn() {
    }

    /**
     * Shows the sheet. webClientId is the Google web client the token is for (its audience);
     * hashedNonce is the SHA-256, in hex, of the nonce the caller sends to Supabase.
     */
    public static void signIn(final Activity activity, final String webClientId,
                              final String hashedNonce, final Callback callback) {
        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                start(activity, webClientId, hashedNonce, callback);
            }
        });
    }

    private static void start(Activity activity, String webClientId, String hashedNonce,
                              final Callback callback) {
        final ExecutorService executor = Executors.newSingleThreadExecutor();
        try {
            GetGoogleIdOption option = new GetGoogleIdOption.Builder()
                    .setFilterByAuthorizedAccounts(false)   // every account on the device, plus "add account"
                    .setServerClientId(webClientId)
                    .setAutoSelectEnabled(false)             // a tap on the button always shows the sheet
                    .setNonce(hashedNonce)
                    .build();
            GetCredentialRequest request = new GetCredentialRequest.Builder()
                    .addCredentialOption(option)
                    .build();

            CredentialManager.create(activity).getCredentialAsync(
                    activity, request, new CancellationSignal(), executor,
                    new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                        @Override
                        public void onResult(GetCredentialResponse response) {
                            try {
                                deliver(response.getCredential(), callback);
                            } finally {
                                executor.shutdown();
                            }
                        }

                        @Override
                        public void onError(GetCredentialException e) {
                            try {
                                String kind = e instanceof GetCredentialCancellationException ? "cancelled" : "failed";
                                callback.onError(kind, e.getClass().getSimpleName() + ": " + e.getMessage());
                            } finally {
                                executor.shutdown();
                            }
                        }
                    });
        } catch (Throwable t) {
            executor.shutdown();
            callback.onError("failed", t.getClass().getSimpleName() + ": " + t.getMessage());
        }
    }

    private static void deliver(Credential credential, Callback callback) {
        if (credential instanceof CustomCredential
                && GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL.equals(credential.getType())) {
            try {
                callback.onToken(GoogleIdTokenCredential.createFrom(credential.getData()).getIdToken());
            } catch (Exception e) {
                callback.onError("failed", "unreadable Google credential: " + e.getMessage());
            }
            return;
        }
        callback.onError("failed", "unexpected credential type " + credential.getType());
    }
}
