using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace UpipeFixtures.Play
{
    public class SmokePlay
    {
        [UnityTest]
        public IEnumerator SmokePasses()
        {
            yield return null;
            Assert.Pass();
        }
    }
}
