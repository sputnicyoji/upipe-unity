using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Upipe.Editor.Tests
{
    public class CaptureCommandsTests
    {
        static readonly byte[] s_PngHeader = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

        static object Capture(JObject parameters) => Capture("game_view", parameters);

        static object Capture(string name, JObject parameters) => CommandRegistry.Find("capture", name).Handler(parameters);

        static bool HasGraphicsDevice => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        // Stands in for a PlayModeView: a window holding RenderTexture fields under a known and an unknown name.
        sealed class FakeView : EditorWindow
        {
            public RenderTexture m_Other;
            public RenderTexture m_TargetTexture;
        }

        [Test]
        public void BadParametersAreRefusedBeforeAnyCapture([Values("game_view", "scene_view")] string name)
        {
            foreach (var parameters in new[]
            {
                new JObject { ["source"] = "window" },
                new JObject { ["save-path"] = "Screenshots/a.jpg" },
                new JObject { ["output-path"] = "a.bmp" },
                new JObject { ["max-resolution"] = "-1" },
                new JObject { ["include-inline-image"] = "maybe" },
                new JObject { ["width"] = "0" },
                new JObject { ["width"] = -1 },
                new JObject { ["height"] = 4097 },
                new JObject { ["height"] = "tall" },
            })
                Assert.AreEqual("Parameter Validation Failed", Assert.Throws<CommandFailure>(() => Capture(name, parameters)).Error, parameters.ToString());
        }

        [Test]
        public void WithoutAGraphicsDeviceTheFailureSaysSo()
        {
            Assume.That(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null, "this Editor has a graphics device (run with -nographics)");
            StringAssert.StartsWith("NO_GPU:", Assert.Throws<CommandFailure>(() => Capture(new JObject())).Message);
        }

        // Without a device: NO_GPU. With one: NO_SCENE_VIEW while no Scene view exists, then the Scene view's camera at the
        // requested size (an Editor that has a Scene view open captures that one, untouched).
        [Test]
        public void SceneViewCaptureRendersItsCameraAtTheRequestedSize()
        {
            if (!HasGraphicsDevice)
            {
                StringAssert.StartsWith("NO_GPU:", Assert.Throws<CommandFailure>(() => Capture("scene_view", new JObject())).Message);
                return;
            }
            SceneView created = null;
            try
            {
                if (SceneView.lastActiveSceneView == null && SceneView.sceneViews.Count == 0)
                {
                    StringAssert.StartsWith("NO_SCENE_VIEW:", Assert.Throws<CommandFailure>(() => Capture("scene_view", new JObject())).Message);
                    created = ScriptableObject.CreateInstance<SceneView>();
                }
                var reply = (JObject)Capture("scene_view", new JObject { ["width"] = 64, ["height"] = "48" });
                Assert.AreEqual("camera 64 48 png", $"{reply["source"]} {reply["width"]} {reply["height"]} {reply["encoding"]}");
                var png = Convert.FromBase64String((string)reply["base64"]);
                CollectionAssert.AreEqual(s_PngHeader, png.Take(8));
                Assert.AreEqual(png.Length, (int)reply["bytes"]);
                if (created != null)
                    StringAssert.StartsWith("NO_SCENE_VIEW:", Assert.Throws<CommandFailure>(() =>
                        Capture("scene_view", new JObject { ["source"] = "screen" })).Message, "a window never shown has drawn nothing");
            }
            finally
            {
                if (created != null)
                    UnityEngine.Object.DestroyImmediate(created);
            }
        }

        // The measured pairs: anything else washes the Scene view grab out on D3D11 or darkens it on Metal (Linear projects).
        [Test]
        public void TheWindowGrabTemporaryFollowsTheGraphicsApi()
        {
            Assert.AreEqual(RenderTextureReadWrite.Linear, CaptureCommands.GrabReadWrite(GraphicsDeviceType.Direct3D11));
            Assert.AreEqual(RenderTextureReadWrite.sRGB, CaptureCommands.GrabReadWrite(GraphicsDeviceType.Metal));
        }

        [Test]
        public void PlayViewTargetPrefersCreatedTexturesKnownNamesAndFocus()
        {
            var first = ScriptableObject.CreateInstance<FakeView>();
            var second = ScriptableObject.CreateInstance<FakeView>();
            var textures = Enumerable.Range(0, 3).Select(_ => new RenderTexture(4, 4, 0)).ToArray();
            try
            {
                first.m_Other = textures[0];
                first.m_TargetTexture = textures[1];
                second.m_TargetTexture = textures[2];
                Assert.IsNull(CaptureCommands.DrawnTexture(first), "a texture never created holds no frame");
                // Which textures hold a frame is given here, so this runs without a graphics device too.
                var drawn = new System.Collections.Generic.HashSet<RenderTexture>();
                bool Drawn(RenderTexture t) => drawn.Contains(t);
                Assert.IsNull(CaptureCommands.PickTarget(new EditorWindow[] { null, first }, null, Drawn));
                drawn.Add(textures[0]);
                Assert.AreSame(textures[0], CaptureCommands.DrawnTexture(first, Drawn), "any drawn RenderTexture is a fallback");
                drawn.Add(textures[1]);
                drawn.Add(textures[2]);
                Assert.AreSame(textures[1], CaptureCommands.DrawnTexture(first, Drawn), "a known target field wins");
                Assert.AreSame(textures[1], CaptureCommands.PickTarget(new EditorWindow[] { first, second }, null, Drawn));
                Assert.AreSame(textures[2], CaptureCommands.PickTarget(new EditorWindow[] { first, second }, second, Drawn), "the focused view wins");
            }
            finally
            {
                foreach (var texture in textures)
                    UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void AssetRelativePathHandlesCaseInsensitiveFilesystems()
        {
            Assert.AreEqual("Assets/Ui/shot.png", CaptureCommands.AssetRelativePath("/proj", "/proj/Assets/Ui/shot.png", caseInsensitive: false));
            Assert.IsNull(CaptureCommands.AssetRelativePath("/proj", "/proj/assets/Ui/shot.png", caseInsensitive: false));

            Assert.AreEqual("Assets/Ui/shot.png", CaptureCommands.AssetRelativePath("/proj", "/proj/assets/Ui/shot.png", caseInsensitive: true));
            Assert.AreEqual("Assets/UI/Shot.png", CaptureCommands.AssetRelativePath(@"C:\Proj\", @"C:\Proj\assets\UI\Shot.png", caseInsensitive: true));

            Assert.IsNull(CaptureCommands.AssetRelativePath("/proj", "/proj/Temp/shot.png", caseInsensitive: true));
            Assert.IsNull(CaptureCommands.AssetRelativePath("/proj", "/proj/AssetsExtra/shot.png", caseInsensitive: true));
        }
    }
}
