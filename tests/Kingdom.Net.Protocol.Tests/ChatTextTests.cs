using Kingdom.Net.Protocol;
using NUnit.Framework;

namespace Kingdom.Net.Protocol.Tests
{
    /// <summary>
    /// The chat text predicate (#409, VULN-004). Both ends run it and the server's run is the one
    /// that counts, so each case here is a string a patched client can send.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class ChatTextTests
    {
        [Test]
        public void Ordinary_Text_Passes_Unchanged()
        {
            Assert.That(ChatText.Clean("gg wp, rematch?"), Is.EqualTo("gg wp, rematch?"));
            Assert.That(ChatText.Clean("नमस्ते"), Is.EqualTo("नमस्ते"), "marks are part of the word in Devanagari");
            Assert.That(ChatText.Clean("nice 🎉"), Is.EqualTo("nice 🎉"), "a whole surrogate pair is kept");
        }

        [Test]
        public void Whitespace_Collapses_And_Newlines_Cannot_Fake_A_Second_Message()
        {
            Assert.That(ChatText.Clean("  hello \n\n  SYSTEM: you won  "), Is.EqualTo("hello SYSTEM: you won"));
            Assert.That(ChatText.Clean("a\tb\r\nc"), Is.EqualTo("a b c"));
        }

        [Test]
        public void Invisible_Characters_Are_Dropped()
        {
            Assert.That(ChatText.Clean("abc‮def"), Is.EqualTo("abcdef"), "a bidi override reverses what follows");
            Assert.That(ChatText.Clean("a​b⁦c⁩"), Is.EqualTo("abc"), "zero-width and isolates");
            Assert.That(ChatText.Clean("x\u0000y\u0007z"), Is.EqualTo("xyz"), "controls");
            Assert.That(ChatText.Clean("pq"), Is.EqualTo("pq"), "private use");
            Assert.That(ChatText.Clean("half\uD83Dpair"), Is.EqualTo("halfpair"), "an unpaired surrogate");
        }

        [Test]
        public void A_Stack_Of_Marks_Is_Cut_Short()
        {
            string zalgo = "a" + new string('́', 40) + "b";
            Assert.That(ChatText.Clean(zalgo), Is.EqualTo("á́́b"));
            Assert.That(ChatText.Clean("́́x"), Is.EqualTo("x"), "a mark with no base is dropped");
            Assert.That(ChatText.Clean("x ́y"), Is.EqualTo("x y"), "nor does one sit on a space");
        }

        [Test]
        public void Nothing_Sendable_Or_Too_Long_Is_Refused()
        {
            Assert.That(ChatText.Clean(null), Is.Null);
            Assert.That(ChatText.Clean(""), Is.Null);
            Assert.That(ChatText.Clean(" \n‮​ "), Is.Null, "nothing visible is left");
            Assert.That(ChatText.Clean(new string('a', ChatText.MaxLength)), Has.Length.EqualTo(ChatText.MaxLength));
            Assert.That(ChatText.Clean(new string('a', ChatText.MaxLength + 1)), Is.Null);
            Assert.That(ChatText.Clean(new string('a', ChatText.MaxLength) + new string('​', 500)),
                Has.Length.EqualTo(ChatText.MaxLength), "length is judged after cleaning");
        }
    }
}
