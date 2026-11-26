using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Upipe.Editor
{
    // `capture game_view`: the Game view's (or Device Simulator's) last rendered frame (source=screen, with overlay UI)
    // or one camera rendered off-screen (source=camera), as PNG. `capture scene_view`: the Scene view's camera rendered
    // off-screen (source=camera, no grid or gizmos) or the Scene view window as drawn (source=screen, gizmos and overlays
    // included). With a save path the reply is the path and sizes only unless include-inline-image is set;
    // `encoding: png` plus `base64` is what `unity mcp` turns into an image.
    internal static class CaptureCommands
    {
        // The public docs' default size for camera captures.
        const int DefaultWidth = 1280;
        const int DefaultHeight = 720;
        const int MaxSize = 4096;

        // Render target fields by the names 2022.3 uses, tried before any other RenderTexture field of the window.
        static readonly string[] s_TargetFieldNames = { "m_TargetTexture", "m_RenderTexture" };

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "capture",
                Name = "game_view",
                Alias = "capture_game_view",
                Description = "Capture the Game view as PNG. With save-path the reply is the file's path and size; without it, the image inline (base64).",
                Tags = new[] { "capture" },
                Parameters = Parameters("screen (default): the Game view or Device Simulator as drawn, overlay UI included; " +
                    "camera: Camera.main (else the first enabled camera) rendered off-screen at width x height."),
                Handler = parameters => Capture(parameters, sceneView: false),
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "capture",
                Name = "scene_view",
                Alias = "capture_scene_view",
                Description = "Capture the Scene view as PNG. With save-path the reply is the file's path and size; without it, the image inline (base64).",
                Tags = new[] { "capture" },
                Parameters = Parameters("camera (default): the Scene view's camera rendered off-screen at width x height, " +
                    "without grid or gizmos; screen: the Scene view window as drawn (gizmos and overlays included), at its own size."),
                Handler = parameters => Capture(parameters, sceneView: true),
            });
        }

        // Declaration order is wire API: width and height come last so positionals keep binding as before.
        static CommandParameter[] Parameters(string source) => new[]
        {
            new CommandParameter { Name = "source", Description = source },
            new CommandParameter { Name = "save-path", Description = "Where to write the PNG: relative to the project root, or absolute. Under Assets/ it is imported." },
            new CommandParameter { Name = "output-path", Description = "Same as save-path (the name the public docs use)." },
            new CommandParameter { Name = "include-inline-image", IsBool = true, Description = "Also return the image inline when saving (default false)." },
            new CommandParameter { Name = "max-resolution", IsInteger = true, Description = "Scale the inline image down so its longest edge is at most this many pixels; the saved file keeps full size." },
            new CommandParameter { Name = "width", IsInteger = true, Description = $"Width of a camera capture in pixels, 1 to {MaxSize} (default {DefaultWidth})." },
            new CommandParameter { Name = "height", IsInteger = true, Description = $"Height of a camera capture in pixels, 1 to {MaxSize} (default {DefaultHeight})." },
        };

        static object Capture(JObject parameters, bool sceneView)
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
            var width = ReadSize(parameters, "width", DefaultWidth);
            var height = ReadSize(parameters, "height", DefaultHeight);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new CommandFailure("Command Execution Failed",
                    "NO_GPU: this Editor runs without a graphics device (-nographics), so there is nothing to capture.");

            var (image, source) = sceneView
                ? CaptureSceneView(requested == "screen", width, height)
                : CaptureGameView(requested, width, height);
            try
            {
                var reply = new JObject
                {
                    ["width"] = image.width,
                    ["height"] = image.height,
                    ["encoding"] = "png",
                    ["source"] = source,
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

        static int ReadSize(JObject parameters, string name, int fallback) =>
            (int)CommandRegistry.ReadInteger(parameters, name, fallback, 1, MaxSize);

        static (Texture2D, string) CaptureGameView(string requested, int width, int height)
        {
            var frame = requested == "camera" ? null : GameViewTexture();
            // Like the docs: with no source given, a Game view that has not drawn yet falls back to a camera.
            if (frame == null && requested == "screen")
                throw new CommandFailure("Command Execution Failed",
                    "NO_GAME_VIEW: no Game view or Device Simulator has drawn a frame to read; open one, or pass --source camera.");
            if (frame != null)
                return (ReadDrawn(frame), "screen");
            var camera = Camera.main != null ? Camera.main : Camera.allCameras.FirstOrDefault();
            if (camera == null)
                throw CommandFailure.InvalidUsage("No camera to capture: the open scenes have no enabled camera.");
            return (RenderCamera(camera, width, height), "camera");
        }

        static (Texture2D, string) CaptureSceneView(bool screen, int width, int height)
        {
            // The last active Scene view first, then any other open one.
            var views = new[] { SceneView.lastActiveSceneView }.Concat(SceneView.sceneViews.OfType<SceneView>())
                .Where(v => v != null && v.camera != null).Distinct().ToList();
            if (views.Count == 0)
                throw new CommandFailure("Command Execution Failed",
                    "NO_SCENE_VIEW: no Scene view is open (a batchmode Editor has none); open one with Window > General > Scene.");
            if (screen)
            {
                // The first one that is its dock's shown tab.
                var drawn = views.Select(GrabWindow).FirstOrDefault(image => image != null);
                if (drawn == null)
                    throw new CommandFailure("Command Execution Failed",
                        "NO_SCENE_VIEW: no Scene view is on screen (hidden behind another tab?); show one, or pass --source camera.");
                return (drawn, "screen");
            }
            // A hidden copy of the Scene view's camera, so the window's own camera, target and aspect stay untouched.
            var copy = EditorUtility.CreateGameObjectWithHideFlags("UpipeSceneCapture", HideFlags.HideAndDontSave, typeof(Camera))
                .GetComponent<Camera>();
            try
            {
                copy.CopyFrom(views[0].camera);
                copy.enabled = false;
                copy.aspect = (float)width / height;
                return (RenderCamera(copy, width, height), "camera");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy.gameObject);
            }
        }

        // shortcut: a window's target holds its rows top-down where UVs start at the top (the Game view's target measured on
        // Metal and D3D11, Edit and Play Mode; a grabbed Scene view on Metal and D3D11); flip there only. Re-check on OpenGL/Vulkan
        // if a capture comes out upside down.
        static Texture2D ReadDrawn(RenderTexture frame, RenderTextureReadWrite readWrite = RenderTextureReadWrite.sRGB) =>
            Read(frame, frame.width, frame.height, SystemInfo.graphicsUVStartsAtTop, readWrite);

        // The temporary GUIView.GrabPixels writes into. In Linear color space the graphics APIs want opposite ones
        // (2022.3.62): D3D11 hands back encoded pixels, which an sRGB temporary encodes again (washed out); Metal hands
        // back decoded ones, which a Linear temporary leaves decoded (too dark). On Metal, Gamma read the same with either.
        // shortcut: measured on D3D11 and Metal only; the other APIs keep sRGB, as before. Re-check D3D12, Vulkan and
        // OpenGL if a scene_view --source screen capture comes out washed out or dark there.
        internal static RenderTextureReadWrite GrabReadWrite(GraphicsDeviceType api) =>
            api == GraphicsDeviceType.Direct3D11 ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;

        // The window as last drawn, grid, gizmos and overlays included, without its tab bar: its host view's back buffer
        // (the Scene view's own scene target reads back empty on Metal). Null when the window is not its dock's shown tab.
        // shortcut: GUIView.GrabPixels, HostView.actualView/screenPosition/borderSize and EditorWindow.m_Parent by
        // reflection (2022.3 names); if an upgrade moves them, scene_view --source screen fails with NO_SCENE_VIEW.
        static Texture2D GrabWindow(EditorWindow window)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var host = typeof(EditorWindow).GetField("m_Parent", any)?.GetValue(window);
            var hostType = host?.GetType();
            if (host == null || !ReferenceEquals(hostType.GetProperty("actualView", any)?.GetValue(host), window))
                return null;
            var grab = hostType.GetMethod("GrabPixels", any, null, new[] { typeof(RenderTexture), typeof(Rect) }, null);
            var screen = hostType.GetProperty("screenPosition", any)?.GetValue(host) as Rect?;
            var border = hostType.GetProperty("borderSize", any)?.GetValue(host) as RectOffset;
            if (grab == null || screen == null || border == null)
                return null;
            var scale = EditorGUIUtility.pixelsPerPoint;
            int OffsetPixels(float points) => Math.Max(0, (int)Math.Round(points * scale));
            int SizePixels(float points) => Math.Max(1, OffsetPixels(points));
            var readWrite = GrabReadWrite(SystemInfo.graphicsDeviceType);
            var whole = RenderTexture.GetTemporary(SizePixels(screen.Value.width), SizePixels(screen.Value.height), 0,
                RenderTextureFormat.ARGB32, readWrite);
            Texture2D upright = null;
            try
            {
                grab.Invoke(host, new object[] { whole, new Rect(0, 0, screen.Value.width, screen.Value.height) });
                upright = ReadDrawn(whole, readWrite);
                // Texture rows count from the bottom: the bottom border comes first, the tab bar is at the top.
                var x = Math.Min(OffsetPixels(border.left), upright.width - 1);
                var y = Math.Min(OffsetPixels(border.bottom), upright.height - 1);
                var width = Math.Min(SizePixels(screen.Value.width - border.horizontal), upright.width - x);
                var height = Math.Min(SizePixels(screen.Value.height - border.vertical), upright.height - y);
                var image = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    image.SetPixels(upright.GetPixels(x, y, width, height));
                    image.Apply();
                    return image;
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(image);
                    throw;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(whole);
                if (upright != null)
                    UnityEngine.Object.DestroyImmediate(upright);
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
            var relative = AssetRelativePath(UpipeServer.ProjectPath, full, UpipeServer.PathsIgnoreCase);
            if (relative != null)
                AssetDatabase.ImportAsset(relative);
            return full;
        }

        internal static string AssetRelativePath(string projectPath, string fullPath, bool caseInsensitive)
        {
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(fullPath))
                return null;
            var root = projectPath.Replace('\\', '/').TrimEnd('/');
            var normalized = fullPath.Replace('\\', '/');
            var assetsPrefix = root + "/Assets/";
            var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!normalized.StartsWith(assetsPrefix, comparison))
                return null;
            var suffix = normalized.Substring(assetsPrefix.Length).TrimStart('/');
            return suffix.Length > 0 ? "Assets/" + suffix : null;
        }

        // The render target of the play view to read: every live PlayModeView (Game view, Device Simulator).
        // shortcut: finds the views and their targets by reflection (2022.3 keeps the target in PlayModeView); if a Unity
        // upgrade moves them, screen captures fail with NO_GAME_VIEW and camera captures still work.
        static RenderTexture GameViewTexture()
        {
            var playModeView = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
            if (playModeView == null)
                return null;
            return PickTarget(Resources.FindObjectsOfTypeAll(playModeView).OfType<EditorWindow>(), EditorWindow.focusedWindow);
        }

        // The focused view first, then Game views, then the rest (Device Simulator); the first with a drawn target wins.
        // `drawn` says whether a texture holds a frame (created, by default; tests pass their own).
        internal static RenderTexture PickTarget(IEnumerable<EditorWindow> views, EditorWindow focused, Func<RenderTexture, bool> drawn = null)
        {
            return views.Where(v => v != null)
                .OrderBy(v => v == focused ? 0 : v.GetType().Name == "GameView" ? 1 : 2)
                .Select(v => DrawnTexture(v, drawn))
                .FirstOrDefault(t => t != null);
        }

        // A drawn RenderTexture held by the window: one of s_TargetFieldNames if any, else the first other one found.
        internal static RenderTexture DrawnTexture(EditorWindow window, Func<RenderTexture, bool> drawn = null)
        {
            drawn = drawn ?? (texture => texture.IsCreated());
            RenderTexture other = null;
            for (var type = window.GetType(); type != null && type != typeof(EditorWindow); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(RenderTexture) || !(field.GetValue(window) is RenderTexture target) ||
                        target == null || !drawn(target))
                        continue;
                    if (s_TargetFieldNames.Contains(field.Name))
                        return target;
                    if (other == null)
                        other = target;
                }
            return other;
        }

        static Texture2D RenderCamera(Camera camera, int width, int height)
        {
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = previous;
                return Read(target, width, height, false);
            }
            finally
            {
                camera.targetTexture = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // Copies a texture into a readable Texture2D of the given size (a blit scales it), upside down when `flip`.
        static Texture2D Read(Texture source, int width, int height, bool flip,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.sRGB)
        {
            var copy = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
            var active = RenderTexture.active;
            try
            {
                if (flip)
                    Graphics.Blit(source, copy, new Vector2(1, -1), new Vector2(0, 1));
                else
                    Graphics.Blit(source, copy);
                RenderTexture.active = copy;
                var image = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    image.Apply();
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(image);
                    throw;
                }
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
