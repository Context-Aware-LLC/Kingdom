using System;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.MigrationParity
{
    /// <summary>
    /// Phase 2a item 1, foundation: the comparator must compare id EQUALITY RELATIONS rather than id
    /// values, which is only possible if something says which canonical values are identities.
    /// These tests pin that the answer is DERIVED from the declared field types plus the existing
    /// inventory-locked manifest — not from a hand-maintained second list, which would be the
    /// document's own organizing bug class (one fact stored twice, nothing checking agreement).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class IdFieldsTests
    {
        [Test]
        public void Every_InstanceId_Typed_Manifest_Field_Is_An_Id_Field()
        {
            // The expectation is computed the same way the production code must: off the declared
            // type. The test's value is not the list — it is that the list is COMPLETE with respect
            // to the manifest, which the inventory lock in turn holds against the real state types.
            var expected = CanonicalStateFixtures.Manifest
                .SelectMany(kv => kv.Value.Select(f => (Type: kv.Key, Field: f)))
                .Where(x => IsInstanceIdField(x.Type, x.Field))
                .Select(x => $"{x.Type.Name}.{x.Field}")
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.That(IdFields.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(expected));
        }

        [Test]
        public void The_Derived_Id_Set_Is_Exactly_The_Six_Known_Identity_Positions()
        {
            // A change here is a real event: an identity position was added to or removed from
            // canonical state. That is hash-canonical work and the comparator's id canonicalization
            // has to be re-read against it, so it is pinned rather than merely derived.
            Assert.That(IdFields.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[]
            {
                "CardInstance.Id",
                "Enchantment.Source",
                "EffectInstance.Source",
                "HeroInstance.Id",
                "OngoingEffectInstance.Id",
                "PendingChoice.Source",
            }.OrderBy(k => k, StringComparer.Ordinal)));
        }

        [Test]
        public void NextInstanceCounter_Is_Not_An_Id_Field()
        {
            // The counter is the id SPACE, not an identity. Canonicalizing it as one would erase the
            // exact divergence grounded's FROST reproducer is about: an engine that mints one extra
            // instance leaves NextInstanceCounter off by one, and that must stay visible as a real
            // difference rather than being renumbered away.
            Assert.That(IdFields.IsIdField(typeof(GameState), "NextInstanceCounter"), Is.False);
        }

        [Test]
        public void An_Ordinary_Int_Field_Is_Not_An_Id_Field()
        {
            Assert.That(IdFields.IsIdField(typeof(CardInstance), "CurrentHealth"), Is.False);
            Assert.That(IdFields.IsIdField(typeof(CardInstance), "Position"), Is.False);
        }

        [Test]
        public void A_Nullable_InstanceId_Is_Still_An_Id_Field()
        {
            // EffectInstance.Source went nullable in Phase 0.5 item 1. A tokenizer that only matched
            // the non-nullable struct would silently stop canonicalizing effect provenance.
            Assert.That(IdFields.IsIdField(typeof(EffectInstance), "Source"), Is.True);
        }

        private static bool IsInstanceIdField(Type t, string field)
        {
            var f = t.GetField(field,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            Assert.That(f, Is.Not.Null, $"{t.Name}.{field} is in the manifest but not on the type.");
            return f!.FieldType == typeof(InstanceId) || f.FieldType == typeof(InstanceId?);
        }
    }
}
