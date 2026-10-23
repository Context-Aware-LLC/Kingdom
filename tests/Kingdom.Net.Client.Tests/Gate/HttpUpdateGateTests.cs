using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.Telemetry;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Gate
{
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpUpdateGateTests
    {
        private const string Base = "https://emberfall.fly.dev";
        private const string Contract = "interfaces.v6";

        private static string Answer(string contract = Contract, int android = 0, int ios = 0)
            => "{\"contract\":\"" + contract + "\",\"minBuildAndroid\":" + android +
               ",\"minBuildIos\":" + ios + "}";

        [TestCase(HttpUpdateGate.Android, 10, UpdateGateVerdict.UpdateRequired)]
        [TestCase(HttpUpdateGate.Android, 11, UpdateGateVerdict.Pass)]
        [TestCase(HttpUpdateGate.Ios, 2, UpdateGateVerdict.UpdateRequired)]
        [TestCase(HttpUpdateGate.Ios, 3, UpdateGateVerdict.Pass)]
        [TestCase("", 0, UpdateGateVerdict.Pass)]
        [TestCase(HttpUpdateGate.Android, HttpUpdateGate.UnknownBuild, UpdateGateVerdict.Pass)]
        public async Task A_build_is_held_to_its_own_platforms_minimum(
            string platform, int storeBuild, UpdateGateVerdict expected)
        {
            var http = new FakeAuthHttp().Reply(200, Answer(android: 11, ios: 3));

            UpdateGateResult result = await new HttpUpdateGate(Base, http)
                .CheckAsync(platform, storeBuild, Contract);

            Assert.That(result.Verdict, Is.EqualTo(expected));
            Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/update-gate"));
        }

        [TestCase("interfaces.v7", UpdateGateVerdict.UpdateRequired)]
        [TestCase("interfaces.v5", UpdateGateVerdict.Pass)]
        [TestCase("something-else", UpdateGateVerdict.Pass)]
        public async Task Only_a_server_contract_AHEAD_of_the_client_demands_an_update(
            string serverContract, UpdateGateVerdict expected)
        {
            var http = new FakeAuthHttp().Reply(200, Answer(contract: serverContract));

            UpdateGateResult result = await new HttpUpdateGate(Base, http)
                .CheckAsync(HttpUpdateGate.Android, 10, Contract);

            Assert.That(result.Verdict, Is.EqualTo(expected));
        }

        [Test]
        public async Task A_dead_network_is_a_block_not_a_pass()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            UpdateGateResult result = await new HttpUpdateGate(Base, http)
                .CheckAsync(HttpUpdateGate.Android, 10, Contract);

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.Unreachable));
        }

        [TestCase(404, "{}")]
        [TestCase(200, "{\"contract\":\"interfaces.v6\"}")]
        [TestCase(200, "not json")]
        public async Task An_answer_it_cannot_read_is_a_block_not_a_pass(int statusCode, string body)
        {
            var http = new FakeAuthHttp().Reply(statusCode, body);

            UpdateGateResult result = await new HttpUpdateGate(Base, http)
                .CheckAsync(HttpUpdateGate.Android, 10, Contract);

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.Unreachable));
        }

        [Test]
        public async Task The_request_carries_the_funnel_session_and_the_device_token()
        {
            var http = new FakeAuthHttp().Reply(200, Answer());

            await new HttpUpdateGate(Base, http, "0123456789abcdef0123456789abcdef", "device-token")
                .CheckAsync(HttpUpdateGate.Android, 10, Contract);

            var headers = http.Requests.Single().Headers;
            Assert.That(headers, Does.Contain(new KeyValuePair<string, string>(
                FunnelHeaders.Session, "0123456789abcdef0123456789abcdef")));
            Assert.That(headers, Does.Contain(new KeyValuePair<string, string>(
                FunnelHeaders.Device, "device-token")));
        }

        [Test]
        public async Task With_no_funnel_keys_the_headers_are_left_off_rather_than_sent_empty()
        {
            var http = new FakeAuthHttp().Reply(200, Answer());

            await new HttpUpdateGate(Base, http).CheckAsync(HttpUpdateGate.Android, 10, Contract);

            var names = http.Requests.Single().Headers.Select(h => h.Key).ToList();
            Assert.That(names, Does.Not.Contain(FunnelHeaders.Session).And.Not.Contain(FunnelHeaders.Device));
        }
    }
}
