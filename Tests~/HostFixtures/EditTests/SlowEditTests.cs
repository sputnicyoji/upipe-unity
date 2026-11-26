using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace UpipeFixtures
{
    // Takes a few seconds, so a detached run can be polled while it is still going.
    public class SlowFixture
    {
        static IEnumerator Wait(double seconds)
        {
            var end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
                yield return null;
        }

        [UnityTest] public IEnumerator First() => Wait(1.5);
        [UnityTest] public IEnumerator Second() => Wait(1.5);
        [UnityTest] public IEnumerator Third() => Wait(1.5);
        [UnityTest] public IEnumerator Fourth() => Wait(1.5);
    }
}
