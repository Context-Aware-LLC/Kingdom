using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Gate
{
    public enum UpdateGateVerdict
    {
        Pass,
        UpdateRequired,
        Unreachable,
    }

    public sealed class UpdateGateResult
    {
        public UpdateGateVerdict Verdict { get; }
        public string Detail { get; }

        private UpdateGateResult(UpdateGateVerdict verdict, string detail)
        {
            Verdict = verdict;
            Detail = detail ?? "";
        }

        public static UpdateGateResult Pass() => new UpdateGateResult(UpdateGateVerdict.Pass, "");

        public static UpdateGateResult UpdateRequired(string detail)
            => new UpdateGateResult(UpdateGateVerdict.UpdateRequired, detail);

        public static UpdateGateResult Unreachable(string detail)
            => new UpdateGateResult(UpdateGateVerdict.Unreachable, detail);
    }

    /// <summary>
    /// Client for the server's unauthenticated <c>GET /update-gate</c>: is this build still allowed
    /// to play, and does the server answer at all.
    ///
    /// <para><b>It fails CLOSED.</b> Anything other than a readable answer is
    /// <see cref="UpdateGateVerdict.Unreachable"/>, never a pass — the game has no offline mode, so
    /// letting a build through on a dead network only moves the failure to the queue, where it
    /// reads as something else.</para>
    ///
    /// <para><b>Two ways to be stale.</b> The store build number is under the platform's minimum,
    /// or the server's wire contract is a later generation than this build's. The second needs no
    /// configuration: a contract bump blocks every older client the moment it deploys. A client
    /// AHEAD of the server passes — that is a build under test against a server not yet deployed,
    /// and telling it to update would send a developer to the store for a build that is not there.</para>
    ///
    /// <para><b>This is a courtesy, not a control.</b> The build number is the client's own claim;
    /// what actually refuses a stale client is <c>MatchGate</c>'s contract check.</para>
    /// </summary>
    public sealed class HttpUpdateGate
    {
        public const string Android = "android";
        public const string Ios = "ios";

        /// <summary>A build that cannot name its store build number — the Editor. Never stale by
        /// number; the contract comparison still applies.</summary>
        public const int UnknownBuild = -1;

        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpUpdateGate(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <param name="platform"><see cref="Android"/>, <see cref="Ios"/>, or anything else for a
        /// platform with no store minimum.</param>
        /// <param name="storeBuild">Android versionCode / iOS build number, or
        /// <see cref="UnknownBuild"/>.</param>
        public async Task<UpdateGateResult> CheckAsync(
            string platform,
            int storeBuild,
            string clientContract,
            CancellationToken cancellationToken = default)
        {
            if (_baseUrl.Length == 0)
                return UpdateGateResult.Unreachable("No server is configured.");

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "application/json"),
            };

            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + "/update-gate", headers, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure)
                return UpdateGateResult.Unreachable(response.Body);
            if (!response.IsSuccess)
                return UpdateGateResult.Unreachable(
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            if (!AuthJson.TryReadTopLevel(response.Body, out var fields) ||
                !AuthJson.TryString(fields, "contract", out string serverContract) ||
                !AuthJson.TryInt64(fields, "minBuildAndroid", out long minAndroid) ||
                !AuthJson.TryInt64(fields, "minBuildIos", out long minIos))
                return UpdateGateResult.Unreachable("Unreadable update-gate answer.");

            long minBuild = platform == Android ? minAndroid : platform == Ios ? minIos : 0;
            if (storeBuild != UnknownBuild && storeBuild < minBuild)
                return UpdateGateResult.UpdateRequired(
                    "build " + storeBuild.ToString(CultureInfo.InvariantCulture) +
                    " is below the minimum " + minBuild.ToString(CultureInfo.InvariantCulture));

            if (TryGeneration(serverContract, out int serverGeneration) &&
                TryGeneration(clientContract, out int clientGeneration) &&
                serverGeneration > clientGeneration)
                return UpdateGateResult.UpdateRequired(
                    "server contract '" + serverContract + "' is newer than '" + clientContract + "'");

            return UpdateGateResult.Pass();
        }

        /// <summary>The N of <c>interfaces.vN</c>. A label that does not end in digits has no
        /// order, and the caller then makes no claim about which end is newer.</summary>
        private static bool TryGeneration(string contract, out int generation)
        {
            generation = 0;
            if (string.IsNullOrEmpty(contract)) return false;
            int start = contract.Length;
            while (start > 0 && contract[start - 1] >= '0' && contract[start - 1] <= '9') start--;
            return start < contract.Length &&
                   int.TryParse(contract.Substring(start), NumberStyles.None,
                       CultureInfo.InvariantCulture, out generation);
        }
    }
}
