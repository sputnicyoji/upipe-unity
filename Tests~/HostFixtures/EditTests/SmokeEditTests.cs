using NUnit.Framework;

namespace UpipeFixtures
{
    public class SmokeEdit
    {
        [Test]
        public void SmokePasses() => Assert.Pass();
    }

    public class CalcTests
    {
        [TestCase(1, 2)]
        [TestCase(3, 4)]
        public void Add(int a, int b) => Assert.AreEqual(a + b, b + a);

        [TestCase("a,b")]
        public void Quote(string text) => Assert.IsTrue(text.Contains(","));

        [Test]
        public void Plain() => Assert.Pass();

        [Test]
        public void PlainSibling() => Assert.Pass();
    }

    public class OtherFixture
    {
        [Test]
        public void Lone() => Assert.Pass();
    }
}
