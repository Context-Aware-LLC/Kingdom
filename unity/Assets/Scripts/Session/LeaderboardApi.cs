using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Kingdom.Session
{
    [Serializable]
    public sealed class LeaderboardResponse
    {
        public string generatedAtUtc;
        public string ratingSystem;
        public LeaderboardRow[] entries;
    }

    [Serializable]
    public sealed class LeaderboardRow
    {
        public int rank;
        public string displayName;
        public int rating;
        public long gamesPlayed;
        public long wins;
        public long losses;
        public long draws;
    }

    /// <summary>What <c>GET /rating</c> answers: the signed-in player's own standing.</summary>
    [Serializable]
    public sealed class OwnRatingResponse
    {
        public string status;
        public int rating;
        public long gamesPlayed;

        /// <summary>Branch on this, never on <see cref="rating"/>: a player with no rated match
        /// behind them is answered with the ladder default, which looks like a rating.</summary>
        public bool IsRanked => status == "ranked";
    }

    public sealed class LeaderboardFetchResult
    {
        private LeaderboardFetchResult(LeaderboardResponse response, string error)
        {
            Response = response;
            Error = error;
        }

        public LeaderboardResponse Response { get; }
        public string Error { get; }
        public bool Ok => Response != null && string.IsNullOrEmpty(Error);
        public static LeaderboardFetchResult Failed(string error) => new LeaderboardFetchResult(null, error);
        public static LeaderboardFetchResult Succeeded(LeaderboardResponse response) => new LeaderboardFetchResult(response, "");
    }

    public static class LeaderboardApi
    {
        public static async Task<LeaderboardFetchResult> FetchAsync(
            string endpoint,
            CancellationToken cancellationToken = default)
        {
            (string body, string error) = await GetAsync(endpoint, null, cancellationToken);
            if (error != null) return LeaderboardFetchResult.Failed(error);

            try
            {
                LeaderboardResponse response = JsonUtility.FromJson<LeaderboardResponse>(body);
                if (response == null)
                    return LeaderboardFetchResult.Failed("The ladder response was unreadable.");

                response.entries ??= Array.Empty<LeaderboardRow>();
                return LeaderboardFetchResult.Succeeded(response);
            }
            catch (Exception ex)
            {
                return LeaderboardFetchResult.Failed(ex.Message);
            }
        }

        /// <summary>
        /// The signed-in player's own standing, or null when it could not be read for any reason.
        /// Null is not "unranked" — that is an answer, and it comes back as a response whose
        /// <see cref="OwnRatingResponse.IsRanked"/> is false.
        /// </summary>
        public static async Task<OwnRatingResponse> FetchOwnRatingAsync(
            string baseUrl,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(accessToken)) return null;

            (string body, string error) = await GetAsync(
                (baseUrl ?? "").TrimEnd('/') + "/rating", accessToken, cancellationToken);
            if (error != null) return null;

            try
            {
                return JsonUtility.FromJson<OwnRatingResponse>(body);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>One GET. Never throws: the body on success, otherwise a player-readable error
        /// and a null body.</summary>
        private static Task<(string body, string error)> GetAsync(
            string endpoint,
            string bearer,
            CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                return Task.FromResult<(string, string)>((null, "The ladder endpoint is not configured."));

            var completion = new TaskCompletionSource<(string, string)>();
            var request = UnityWebRequest.Get(endpoint);
            request.timeout = 15;
            if (!string.IsNullOrEmpty(bearer))
                request.SetRequestHeader("Authorization", "Bearer " + bearer);

            CancellationTokenRegistration registration = default;
            if (cancellationToken.CanBeCanceled)
                registration = cancellationToken.Register(() => { try { request.Abort(); } catch { } });

            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch (Exception ex)
            {
                registration.Dispose();
                request.Dispose();
                completion.SetResult((null, ex.Message));
                return completion.Task;
            }

            operation.completed += _ =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.SetResult((null, "cancelled"));
                        return;
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        completion.SetResult((null,
                            request.responseCode > 0
                                ? $"The ladder returned {request.responseCode}."
                                : "The ladder could not be reached."));
                        return;
                    }

                    completion.SetResult((request.downloadHandler?.text ?? "", null));
                }
                catch (Exception ex)
                {
                    completion.SetResult((null, ex.Message));
                }
                finally
                {
                    registration.Dispose();
                    request.Dispose();
                }
            };

            return completion.Task;
        }
    }
}
