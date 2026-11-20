using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Upipe.Editor
{
    // `capture game_view`: the Game view's last rendered frame (source=screen, with overlay UI) or one camera rendered
    // off-screen (source=camera), as PNG. With a save path the reply is the path and sizes only unless
    // include-inline-image is set; `encoding: png` plus `base64` is what `unity mcp` turns into an image
    // (evidence/unity-pipeline-2022/issue6-cli-test-capture-contract.log).
    internal static class CaptureCommands
    {
        // The public docs' default size for camera captures.
        const int CameraWidth = 1280;
        const int CameraHeight = 720;

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "capture",
                Name = "game_view",
                Alias = "capture_game_view",
                Description = "Capture the Game view as PNG. With save-path the reply is the file's path and size; without it, the image inline (base64).",
                Tags = new[] { "capture" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "source", Description = "screen (default): the Game view as drawn, overlay UI included; camera: Camera.main (else the first enabled camera) rendered off-screen at 1280x720." },
                    new CommandParameter { Name = "save-path", Description = "Where to write the PNG: relative to the project root, or absolute. Under Assets/ it is imported." },
                    new CommandParameter { Name = "output-path", Description = "Same as save-path (the name the public docs use)." },
                    new CommandParameter { Name = "include-inline-image", IsBool = true, Description = "Also return the image inline when saving (default false)." },
                    new CommandParameter { Name = "max-resolution", IsInteger = true, Description = "Scale the inline image down so its longest edge is at most this many pixels; the saved file keeps full size." },
                },
                Handler = Capture,
            });
        }

        static object Capture(JObject parameters)
        {
            var requested = ((string)parameters["source"] ?? "").ToLowerInvariant();
            if (requested.Length > 0 && requested != "screen" && requested != "camera")
                throw CommandFailure.InvalidUsage("source must be screen or camera.");
            var path = (string)parameters["save-path"] ?? (string)parameters["output-path"];
            if (path != null && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw CommandFailure.InvalidUsage("The save path must end in .png.");
            var inline = CommandRegistry.ReadBool(parameters, "include-inline-image", false) || path == null;
            var maxResolution = CommandRegistry.ReadInteger(parameters, "max-resolution", 0);
            if (maxResolution < 0)
                throw CommandFailure.InvalidUsage("max-resolution must be 0 (no limit) or more.");
            var inlineLimit = (int)Math.Min(maxResolution, int.MaxValue);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new CommandFailure("Command Execution Failed",
                    "NO_GPU: this Editor runs without a graphics device (-nographics), so there is nothing to capture.");

            var frame = requested == "camera" ? null : GameViewTexture();
            // Like the docs: with no source given, a Game view that has not drawn yet falls back to a camera.
            if (frame == null && requested == "screen")
                throw new CommandFailure("Command Execution Failed",
                    "NO_GAME_VIEW: no Game view has drawn a frame to read; open the Game view, or pass --source camera.");
            // shortcut: the Game view's target holds its rows top-down where UVs start at the top (Metal, measured on macOS;
            // D3D is assumed the same); flip there only. Re-check on OpenGL/Vulkan if a capture comes out upside down.
            var image = frame != null ? Read(frame, frame.width, frame.height, SystemInfo.graphicsUVStartsAtTop) : RenderCamera();
            try
            {
                var reply = new JObject
                {
                    ["width"] = image.width,
                    ["height"] = image.height,
                    ["encoding"] = "png",
                    ["source"] = frame != null ? "screen" : "camera",
                };
                // The full-size PNG is encoded only when it is saved or returned as is: encoding a large frame is the
                // slowest step on the main thread.
                var saved = path != null ? image.EncodeToPNG() : null;
                if (saved != null)
                    reply["savedPath"] = Save(path, saved);
                var shown = inline ? Inline(image, saved, inlineLimit, reply) : null;
                if (shown != null)
                    reply["base64"] = Convert.ToBase64String(shown);
                // The saved file's size, or the inline image's when nothing is saved.
                reply["bytes"] = (saved ?? shown).Length;
                return reply;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        // `fullSize` is the full-size PNG when one was already encoded.
        static byte[] Inline(Texture2D image, byte[] fullSize, int maxResolution, JObject reply)
        {
            var longest = Math.Max(image.width, image.height);
            if (maxResolution == 0 || longest <= maxResolution)
                return fullSize ?? image.EncodeToPNG();
            var width = Math.Max(1, (int)Math.Round((double)image.width * maxResolution / longest));
            var height = Math.Max(1, (int)Math.Round((double)image.height * maxResolution / longest));
            var scaled = Read(image, width, height, false);
            try
            {
                reply["inlineWidth"] = width;
                reply["inlineHeight"] = height;
                return scaled.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scaled);
            }
        }

        static string Save(string path, byte[] png)
        {
            var full = UpipeServer.ResolveProjectPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllBytes(full, png);
            var assets = Path.Combine(UpipeServer.ProjectPath, "Assets") + Path.DirectorySeparatorChar;
            if (full.StartsWith(assets, StringComparison.Ordinal))
                AssetDatabase.ImportAsset(full.Substring(UpipeServer.ProjectPath.Length + 1).Replace('\\', '/'));
            return full;
        }

        // The render target of the first Game view that has one.
        // shortcut: finds the target as the Game view's RenderTexture field by reflection (2022.3 keeps it in PlayModeView);
        // if a Unity upgrade moves it, screen captures fail with NO_GAME_VIEW and camera captures still work.
        static RenderTexture GameViewTexture()
        {
            var gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameView == null)
                return null;
            foreach (var view in Resources.FindObjectsOfTypeAll(gameView))
                for (var type = gameView; type != null && type != typeof(EditorWindow); type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (field.FieldType == typeof(RenderTexture) && field.GetValue(view) is RenderTexture target && target != null && target.IsCreated())
                            return target;
            return null;
        }

        static Texture2D RenderCamera()
        {
            var camera = Camera.main != null ? Camera.main : Camera.allCameras.FirstOrDefault();
            if (camera == null)
                throw CommandFailure.InvalidUsage("No camera to capture: the open scenes have no enabled camera.");
            var target = RenderTexture.GetTemporary(CameraWidth, CameraHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                camera.Render();
            }
            finally
            {
                camera.targetTexture = previous;
            }
            try
            {
                return Read(target, CameraWidth, CameraHeight, false);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // Copies a texture into a readable Texture2D of the given size (a blit scales it), upside down when `flip`.
        static Texture2D Read(Texture source, int width, int height, bool flip)
        {
            var copy = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var active = RenderTexture.active;
            try
            {
                if (flip)
                    Graphics.Blit(source, copy, new Vector2(1, -1), new Vector2(0, 1));
                else
                    Graphics.Blit(source, copy);
                RenderTexture.active = copy;
                var image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(copy);
            }
        }
    }
}
