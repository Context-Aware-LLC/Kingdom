using System;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Kingdom.Session
{
    /// <summary>What <c>GET /account</c> answers: the settings Account view's server facts.</summary>
    [Serializable]
    public sealed class AccountInfoResponse
    {
        public string status;
        public string email;

        /// <summary>ISO 8601 UTC, or empty when the server had no answer.</summary>
        public string createdAt;

        /// <summary>The registration date, or null. Null is "unknown", never a date to show.</summary>
        public DateTime? RegisteredUtc =>
            status == "ready" && DateTime.TryParse(createdAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
                ? t
                : (DateTime?)null;
    }

    public static class AccountInfoApi
    {
        /// <summary>The bearer's account facts, or null when they could not be read for any reason.
        /// Never throws: the Account view shows what it already knows and leaves the rest out.</summary>
        public static Task<AccountInfoResponse> FetchAsync(string baseUrl, string accessToken)
        {
            var completion = new TaskCompletionSource<AccountInfoResponse>();
            string endpoint = (baseUrl ?? "").TrimEnd('/') + "/account";
            if (string.IsNullOrEmpty(accessToken) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                completion.SetResult(null);
                return completion.Task;
            }

            var request = UnityWebRequest.Get(endpoint);
            request.timeout = 15;
            request.SetRequestHeader("Authorization", "Bearer " + accessToken);
            try
            {
                request.SendWebRequest().completed += _ =>
                {
                    try
                    {
                        completion.SetResult(request.result == UnityWebRequest.Result.Success
                            ? JsonUtility.FromJson<AccountInfoResponse>(request.downloadHandler?.text ?? "")
                            : null);
                    }
                    catch (Exception)
                    {
                        completion.TrySetResult(null);
                    }
                    finally
                    {
                        request.Dispose();
                    }
                };
            }
            catch (Exception)
            {
                request.Dispose();
                completion.TrySetResult(null);
            }
            return completion.Task;
        }
    }
}
