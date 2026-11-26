// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace UpipeFixtures.Play
{
    // Slow enough in Play Mode that test cancel can stop a PlayMode run while its tests are going.
    public class SlowPlay
    {
        [UnityTest] public IEnumerator First() { yield return new WaitForSecondsRealtime(1.5f); }
        [UnityTest] public IEnumerator Second() { yield return new WaitForSecondsRealtime(1.5f); }
        [UnityTest] public IEnumerator Third() { yield return new WaitForSecondsRealtime(1.5f); }
        [UnityTest] public IEnumerator Fourth() { yield return new WaitForSecondsRealtime(1.5f); }
    }
}
