#import <Foundation/Foundation.h>
#import <GameKit/GameKit.h>

// Game Center bridge for Kingdom.Session.GameCenterLadder (GameCenterLadder.cs). Two entry
// points, both called on Unity's main thread, which on iOS is the app's main thread — the same
// one GameKit invokes authenticateHandler on. Nothing here is touched from anywhere else, so the
// statics below need no lock.
//
// THERE IS NO CALLBACK INTO C#. Sign-in finishes whenever GameKit says it does, usually after the
// rating has already arrived, so a score submitted before then is held here and sent from the
// handler. That keeps the managed side free of a reverse P/Invoke.

static BOOL sHasPendingScore = NO;
static long long sPendingScore = 0;
static NSString* sPendingLeaderboardId = nil;

static void KobiSubmit(long long score, NSString* leaderboardId)
{
    [GKLeaderboard submitScore:(NSInteger)score
                       context:0
                        player:[GKLocalPlayer localPlayer]
                leaderboardIDs:@[ leaderboardId ]
             completionHandler:^(NSError* _Nullable error) {
        if (error != nil)
            NSLog(@"[GameCenter] score submit failed: %@", error.localizedDescription);
    }];
}

extern "C" {

void KobiGameCenter_Authenticate(void)
{
    GKLocalPlayer* player = [GKLocalPlayer localPlayer];

    // Set once per launch. GameKit keeps the handler and calls it again by itself when the app
    // returns to the foreground or the player changes account.
    if (player.authenticateHandler != nil) return;

    player.authenticateHandler = ^(UIViewController* _Nullable viewController, NSError* _Nullable error) {
        // A non-nil viewController means "nobody is signed in — present me". It is deliberately
        // NOT presented: see the "Sign-in is silent" note in GameCenterLadder.cs.
        if (![GKLocalPlayer localPlayer].isAuthenticated) return;
        if (!sHasPendingScore) return;

        sHasPendingScore = NO;
        KobiSubmit(sPendingScore, sPendingLeaderboardId);
    };
}

void KobiGameCenter_SubmitScore(long long score, const char* leaderboardId)
{
    if (leaderboardId == NULL) return;
    NSString* identifier = [NSString stringWithUTF8String:leaderboardId];
    if (identifier == nil) return;

    if ([GKLocalPlayer localPlayer].isAuthenticated)
    {
        KobiSubmit(score, identifier);
        return;
    }

    // copy, not a plain assignment: it holds the string whether or not this file is built with ARC.
    sPendingScore = score;
    sPendingLeaderboardId = [identifier copy];
    sHasPendingScore = YES;
}

}
