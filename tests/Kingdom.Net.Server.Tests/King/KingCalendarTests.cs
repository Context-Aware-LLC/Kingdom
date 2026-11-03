using System;
using Kingdom.Net.Server.King;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    [TestFixture]
    public sealed class KingCalendarTests
    {
        private static long T(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeMilliseconds();

        private static readonly long One = T("2026-11-03T00:00Z");

        [Test]
        public void Reign_one_begins_at_the_configured_instant()
            => Assert.That(KingCalendar.ReignAt(T("2026-11-03T00:00Z"), One), Is.EqualTo(1));

        [Test]
        public void Before_reign_one_there_is_no_reign()
            => Assert.That(KingCalendar.ReignAt(T("2026-11-02T23:59Z"), One), Is.EqualTo(0));

        [Test]
        public void Reign_one_lasts_until_the_next_Tuesday()
        {
            Assert.That(KingCalendar.ReignAt(T("2026-11-09T23:59Z"), One), Is.EqualTo(1));
            Assert.That(KingCalendar.ReignAt(T("2026-11-10T00:00Z"), One), Is.EqualTo(2));
        }

        [Test]
        public void StartMs_inverts_ReignAt()
        {
            Assert.That(KingCalendar.StartMs(1, One), Is.EqualTo(One));
            Assert.That(KingCalendar.StartMs(3, One), Is.EqualTo(T("2026-11-17T00:00Z")));
            Assert.That(KingCalendar.ReignAt(KingCalendar.StartMs(40, One), One), Is.EqualTo(40));
            Assert.That(KingCalendar.ReignAt(KingCalendar.StartMs(40, One) - 1, One), Is.EqualTo(39));
        }

        [Test]
        public void Only_Tuesday_midnight_is_a_valid_reign_start()
        {
            Assert.That(KingCalendar.IsTuesdayMidnightUtc(One), Is.True);
            Assert.That(KingCalendar.IsTuesdayMidnightUtc(T("2026-11-04T00:00Z")), Is.False);
            Assert.That(KingCalendar.IsTuesdayMidnightUtc(T("2026-11-03T00:00:01Z")), Is.False);
        }

        [Test]
        public void Env_parser_accepts_a_Tuesday_and_treats_unset_as_no_king()
        {
            Assert.That(KingEnv.ParseReignOne("2026-11-03T00:00:00Z"), Is.EqualTo(One));
            Assert.That(KingEnv.ParseReignOne(null), Is.Null);
            Assert.That(KingEnv.ParseReignOne("  "), Is.Null);
        }

        [Test]
        public void Boot_refuses_a_reign_one_that_is_not_a_Tuesday()
        {
            var err = new System.IO.StringWriter();
            var old = Console.Error;
            Console.SetError(err);
            try
            {
                Assert.That(KingEnv.ParseReignOne("2026-11-04T00:00:00Z"), Is.Null);
                Assert.That(KingEnv.ParseReignOne("not a date"), Is.Null);
            }
            finally { Console.SetError(old); }
            Assert.That(err.ToString(), Does.Contain("KINGDOM_REIGN_ONE"));
        }
    }
}
