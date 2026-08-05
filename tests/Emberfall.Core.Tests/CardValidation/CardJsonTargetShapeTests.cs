using Emberfall.Core.Cards;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class CardJsonTargetShapeTests
    {
        [TestCase("1", "target must be an object")]
        [TestCase("{}", "target.selector must be a string")]
        [TestCase("{\"selector\":1}", "target.selector must be a string")]
        [TestCase("{\"selector\":\"ANY_MINION\",\"filter\":1}", "target.filter must be an object")]
        [TestCase("{\"selector\":\"ANY_MINION\",\"filter\":{\"damaged\":\"true\"}}",
            "target.filter.damaged must be a boolean")]
        [TestCase("{\"selector\":\"ANY_MINION\",\"filter\":{\"tribe\":1}}",
            "target.filter.tribe must be a string")]
        public void Rejects_Malformed_Target_And_Filter_Shapes(string target, string message)
        {
            string json = "{\"id\":\"malformed_target\",\"type\":\"SPELL\",\"effects\":[{" +
                "\"trigger\":\"ON_CAST\",\"action\":\"DRAW\",\"target\":" + target + "}]}";

            var ex = Assert.Throws<System.FormatException>(() => CardJson.Deserialize(json));

            Assert.That(ex!.Message, Is.EqualTo($"Card JSON {message}."));
        }
    }
}
