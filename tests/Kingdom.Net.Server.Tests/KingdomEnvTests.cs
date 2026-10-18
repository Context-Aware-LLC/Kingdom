using System;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>The rename from EMBERFALL_ to KINGDOM_ (2026-10-18): a deploy still holding the
    /// old names must come up configured, not empty.</summary>
    [TestFixture]
    [Category("Unit")]
    [NonParallelizable]
    public sealed class KingdomEnvTests
    {
        private const string Name = "KINGDOM_ENV_TEST_VALUE";
        private const string OldName = "EMBERFALL_ENV_TEST_VALUE";

        [TearDown]
        public void Clear()
        {
            Environment.SetEnvironmentVariable(Name, null);
            Environment.SetEnvironmentVariable(OldName, null);
        }

        [Test]
        public void The_Old_Name_Still_Answers()
        {
            Environment.SetEnvironmentVariable(OldName, "old");
            Assert.That(KingdomEnv.Get(Name), Is.EqualTo("old"));
        }

        [Test]
        public void The_New_Name_Wins()
        {
            Environment.SetEnvironmentVariable(OldName, "old");
            Environment.SetEnvironmentVariable(Name, "new");
            Assert.That(KingdomEnv.Get(Name), Is.EqualTo("new"));
        }

        [Test]
        public void Blank_Is_Unset()
        {
            Environment.SetEnvironmentVariable(Name, "  ");
            Assert.That(KingdomEnv.Get(Name), Is.Null);
        }
    }
}
