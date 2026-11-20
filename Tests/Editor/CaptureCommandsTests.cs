using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Upipe.Editor.Tests
{
    public class CaptureCommandsTests
    {
        static object Capture(JObject parameters) => CommandRegistry.Find("capture", "game_view").Handler(parameters);

        [Test]
        public void BadParametersAreRefusedBeforeAnyCapture()
        {
            foreach (var parameters in new[]
            {
                new JObject { ["source"] = "window" },
                new JObject { ["save-path"] = "Screenshots/a.jpg" },
                new JObject { ["output-path"] = "a.bmp" },
                new JObject { ["max-resolution"] = "-1" },
                new JObject { ["include-inline-image"] = "maybe" },
            })
                Assert.AreEqual("Parameter Validation Failed", Assert.Throws<CommandFailure>(() => Capture(parameters)).Error, parameters.ToString());
        }

        [Test]
        public void WithoutAGraphicsDeviceTheFailureSaysSo()
        {
            Assume.That(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null, "this Editor has a graphics device (run with -nographics)");
            StringAssert.StartsWith("NO_GPU:", Assert.Throws<CommandFailure>(() => Capture(new JObject())).Message);
        }
    }
}
