using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Emberfall.Session
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
        public static Task<LeaderboardFetchResult> FetchAsync(
            string endpoint,
            CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                return Task.FromResult(LeaderboardFetchResult.Failed("The ladder endpoint is not configured."));

            var completion = new TaskCompletionSource<LeaderboardFetchResult>();
            var request = UnityWebRequest.Get(endpoint);
            request.timeout = 15;

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
                completion.SetResult(LeaderboardFetchResult.Failed(ex.Message));
                return completion.Task;
            }

            operation.completed += _ =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.SetResult(LeaderboardFetchResult.Failed("cancelled"));
                        return;
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        completion.SetResult(LeaderboardFetchResult.Failed(
                            request.responseCode > 0
                                ? $"The ladder returned {request.responseCode}."
                                : "The ladder could not be reached."));
                        return;
                    }

                    LeaderboardResponse response = JsonUtility.FromJson<LeaderboardResponse>(
                        request.downloadHandler?.text ?? "");
                    if (response == null)
                    {
                        completion.SetResult(LeaderboardFetchResult.Failed("The ladder response was unreadable."));
                        return;
                    }

                    response.entries ??= Array.Empty<LeaderboardRow>();
                    completion.SetResult(LeaderboardFetchResult.Succeeded(response));
                }
                catch (Exception ex)
                {
                    completion.SetResult(LeaderboardFetchResult.Failed(ex.Message));
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
