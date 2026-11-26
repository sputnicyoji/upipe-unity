using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace UpipeFixtures
{
    // Reloads the domain halfway through, so a synchronous test run waiting on it is cut off. Named only.
    public class ReloadFixture
    {
        [UnityTest, Explicit("named only: it reloads the domain")]
        public IEnumerator ReloadsTheDomain()
        {
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
        }
    }
}
