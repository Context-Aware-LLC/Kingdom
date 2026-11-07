using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    public sealed class BackgroundLoadSequenceTests
    {
        private readonly List<BackgroundTier> _requests = new();
        private readonly List<string> _painted = new();
        private TaskCompletionSource<string?> _remote = null!;
        private BackgroundLoadSequence<string> _loader = null!;
        private double _time;

        [SetUp]
        public void SetUp()
        {
            _requests.Clear();
            _painted.Clear();
            _time = 0;
            _remote = new TaskCompletionSource<string?>();
            _loader = new BackgroundLoadSequence<string>((tier, _) =>
            {
                _requests.Add(tier);
                return tier == BackgroundTier.Mobile ? Task.FromResult<string?>("mobile") : _remote.Task;
            }, () => _time);
        }

        [Test]
        public async Task Phone_never_requests_remote_art()
        {
            await _loader.LoadAsync("Login", BackgroundTier.Mobile, _painted.Add);
            Assert.That(_requests, Is.EqualTo(new[] { BackgroundTier.Mobile }));
            Assert.That(_painted, Is.EqualTo(new[] { "mobile" }));
        }

        [Test]
        public async Task Tablet_paints_fallback_while_download_is_pending_then_upgrades()
        {
            Task pending = _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            Assert.That(pending.IsCompleted, Is.False);
            Assert.That(_painted, Is.EqualTo(new[] { "mobile" }));
            _remote.SetResult("large");
            await pending;
            Assert.That(_painted, Is.EqualTo(new[] { "mobile", "large" }));
        }

        [Test]
        public async Task Screens_share_download_and_reuse_large_art_without_a_fallback_flash()
        {
            Task first = _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            Task second = _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _ => { });
            Assert.That(_requests.Count, Is.EqualTo(2));
            _remote.SetResult("large");
            await Task.WhenAll(first, second);
            _painted.Clear();
            await _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            Assert.That(_painted, Is.EqualTo(new[] { "large" }));
            Assert.That(_requests.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task Offline_keeps_fallback_and_later_visit_can_retry_without_request_storm()
        {
            _remote.SetResult(null);
            await _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            await _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            Assert.That(_painted, Is.EqualTo(new[] { "mobile", "mobile" }));
            Assert.That(_requests.Count, Is.EqualTo(2));
            _time = 61;
            _remote = new TaskCompletionSource<string?>();
            _remote.SetResult("large");
            await _loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            Assert.That(_requests.Count, Is.EqualTo(3));
            Assert.That(_painted[^1], Is.EqualTo("large"));
        }

        [Test]
        public async Task Missing_local_art_does_not_prevent_remote_recovery()
        {
            var loader = new BackgroundLoadSequence<string>((tier, _) =>
                Task.FromResult(tier == BackgroundTier.Mobile ? null : "large"), () => 0);
            await loader.LoadAsync("Login", BackgroundTier.DesktopTablet, _painted.Add);
            Assert.That(_painted, Is.EqualTo(new[] { "large" }));
        }
    }
}
