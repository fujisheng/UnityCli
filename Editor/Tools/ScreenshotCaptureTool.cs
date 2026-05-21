using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("screenshot.capture", Description = "Capture camera, GameView, SceneView, Unity Editor window, editor window, or desktop region screenshots", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets | ToolCapabilities.PlayMode, Category = "editor")]
    public sealed class ScreenshotCaptureTool : IUnityCliTool
    {
        const string DefaultMode = "game_view";
        const string DefaultFormat = "png";
        const int DefaultQuality = 90;
        const int DefaultScale = 1;
        const int DefaultFallbackWidth = 1280;
        const int DefaultFallbackHeight = 720;

        static readonly string[] SupportedModes =
        {
            "camera",
            "game_view",
            "scene_view",
            "editor_window",
            "unity_editor",
            "desktop_region"
        };

        static readonly string[] SupportedFormats =
        {
            "png",
            "jpg",
            "jpeg",
            "exr",
            "tga"
        };

        public string Id => "screenshot.capture";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Capture camera, GameView, SceneView, Unity Editor window, editor window, or desktop region screenshots",
                category = "editor",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets | ToolCapabilities.PlayMode,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor { name = "mode", type = "string", description = "Capture mode: camera/game_view/scene_view/editor_window/unity_editor/desktop_region", required = false, defaultValue = DefaultMode },
                    new ParamDescriptor { name = "path", type = "string", description = "Output image path. Relative paths are resolved from the Unity project root.", required = true },
                    new ParamDescriptor { name = "format", type = "string", description = "Output format: png/jpg/jpeg/exr/tga. Defaults to path extension or png.", required = false, defaultValue = DefaultFormat },
                    new ParamDescriptor { name = "quality", type = "integer", description = "JPG quality from 1 to 100", required = false, defaultValue = DefaultQuality },
                    new ParamDescriptor { name = "width", type = "integer", description = "Output width in pixels. 0 uses source width.", required = false, defaultValue = 0 },
                    new ParamDescriptor { name = "height", type = "integer", description = "Output height in pixels. 0 uses source height.", required = false, defaultValue = 0 },
                    new ParamDescriptor { name = "scale", type = "integer", description = "GameView super-size or source scale. Minimum 1.", required = false, defaultValue = DefaultScale },
                    new ParamDescriptor { name = "overwrite", type = "boolean", description = "Overwrite existing output file", required = false, defaultValue = true },
                    new ParamDescriptor { name = "createDirectory", type = "boolean", description = "Create output directory if missing", required = false, defaultValue = true },
                    new ParamDescriptor { name = "allowAbsolutePath", type = "boolean", description = "Allow writing to an absolute path outside the project root", required = false, defaultValue = false },
                    new ParamDescriptor { name = "cameraName", type = "string", description = "Camera name for mode=camera", required = false },
                    new ParamDescriptor { name = "cameraTag", type = "string", description = "Camera tag for mode=camera", required = false },
                    new ParamDescriptor { name = "includeInactive", type = "boolean", description = "Include inactive cameras when resolving mode=camera", required = false, defaultValue = false },
                    new ParamDescriptor { name = "transparent", type = "boolean", description = "Render camera with transparent background when possible", required = false, defaultValue = false },
                    new ParamDescriptor { name = "windowType", type = "string", description = "EditorWindow type or friendly name for mode=editor_window", required = false, defaultValue = "GameView" },
                    new ParamDescriptor { name = "windowTitle", type = "string", description = "Optional title substring for mode=editor_window", required = false },
                    new ParamDescriptor { name = "includeWindowChrome", type = "boolean", description = "Capture EditorWindow chrome where the backend supports it", required = false, defaultValue = false },
                    new ParamDescriptor { name = "x", type = "integer", description = "Desktop region x coordinate for mode=desktop_region", required = false, defaultValue = 0 },
                    new ParamDescriptor { name = "y", type = "integer", description = "Desktop region y coordinate for mode=desktop_region", required = false, defaultValue = 0 },
                    new ParamDescriptor { name = "regionWidth", type = "integer", description = "Desktop region width for mode=desktop_region", required = false, defaultValue = 0 },
                    new ParamDescriptor { name = "regionHeight", type = "integer", description = "Desktop region height for mode=desktop_region", required = false, defaultValue = 0 }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (!StateGuard.EnsureReady(context, out var error))
            {
                return error;
            }

            if (!TryReadOptions(args, out var options, out error))
            {
                return error;
            }

            if (!TryResolveOutputPath(options, out var outputPath, out error))
            {
                return error;
            }

            try
            {
                var warnings = new List<string>();
                Texture2D texture = null;
                Texture2D outputTexture = null;
                try
                {
                    texture = Capture(options, warnings);
                    if (texture == null)
                    {
                        return ToolResult.Error("tool_execution_failed", "截图失败，未得到纹理。", new { options.mode });
                    }

                    outputTexture = ResizeIfNeeded(texture, options.width, options.height);
                    var finalTexture = outputTexture ?? texture;
                    var bytes = Encode(finalTexture, options.format, options.quality, warnings);
                    File.WriteAllBytes(outputPath, bytes);

                    return ToolResult.Ok(new
                    {
                        path = outputPath,
                        width = finalTexture.width,
                        height = finalTexture.height,
                        mode = options.mode,
                        format = NormalizeOutputFormat(options.format),
                        quality = options.quality,
                        exactSize = options.width <= 0 && options.height <= 0 || finalTexture.width == options.width && finalTexture.height == options.height,
                        source = ResolveSourceLabel(options),
                        warnings = warnings.ToArray()
                    }, "截图已保存。");
                }
                finally
                {
                    if (outputTexture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(outputTexture);
                    }

                    if (texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"截图失败：{exception.Message}", exception.ToString());
            }
        }

        static bool TryReadOptions(Dictionary<string, object> args, out CaptureOptions options, out ToolResult error)
        {
            options = null;
            error = null;

            if (!ArgsHelper.TryGetOptional(args, "mode", DefaultMode, out string mode, out error)
                || !ArgsHelper.TryGetRequired(args, "path", out string path, out error)
                || !ArgsHelper.TryGetOptional(args, "format", string.Empty, out string format, out error)
                || !ArgsHelper.TryGetOptional(args, "quality", DefaultQuality, out int quality, out error)
                || !ArgsHelper.TryGetOptional(args, "width", 0, out int width, out error)
                || !ArgsHelper.TryGetOptional(args, "height", 0, out int height, out error)
                || !ArgsHelper.TryGetOptional(args, "scale", DefaultScale, out int scale, out error)
                || !ArgsHelper.TryGetOptional(args, "overwrite", true, out bool overwrite, out error)
                || !ArgsHelper.TryGetOptional(args, "createDirectory", true, out bool createDirectory, out error)
                || !ArgsHelper.TryGetOptional(args, "allowAbsolutePath", false, out bool allowAbsolutePath, out error)
                || !ArgsHelper.TryGetOptional(args, "cameraName", string.Empty, out string cameraName, out error)
                || !ArgsHelper.TryGetOptional(args, "cameraTag", string.Empty, out string cameraTag, out error)
                || !ArgsHelper.TryGetOptional(args, "includeInactive", false, out bool includeInactive, out error)
                || !ArgsHelper.TryGetOptional(args, "transparent", false, out bool transparent, out error)
                || !ArgsHelper.TryGetOptional(args, "windowType", "GameView", out string windowType, out error)
                || !ArgsHelper.TryGetOptional(args, "windowTitle", string.Empty, out string windowTitle, out error)
                || !ArgsHelper.TryGetOptional(args, "includeWindowChrome", false, out bool includeWindowChrome, out error)
                || !ArgsHelper.TryGetOptional(args, "x", 0, out int x, out error)
                || !ArgsHelper.TryGetOptional(args, "y", 0, out int y, out error)
                || !ArgsHelper.TryGetOptional(args, "regionWidth", 0, out int regionWidth, out error)
                || !ArgsHelper.TryGetOptional(args, "regionHeight", 0, out int regionHeight, out error))
            {
                return false;
            }

            mode = NormalizeToken(mode);
            if (!SupportedModes.Contains(mode, StringComparer.Ordinal))
            {
                error = ToolResult.Error("invalid_parameter", $"不支持的截图模式 '{mode}'。", new
                {
                    parameter = "mode",
                    supported = SupportedModes
                });
                return false;
            }

            format = ResolveFormat(path, format);
            if (!SupportedFormats.Contains(format, StringComparer.Ordinal))
            {
                error = ToolResult.Error("invalid_parameter", $"不支持的图片格式 '{format}'。", new
                {
                    parameter = "format",
                    supported = SupportedFormats
                });
                return false;
            }

            if (quality < 1 || quality > 100)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'quality' 必须在 1 到 100 之间。", new { quality });
                return false;
            }

            if (width < 0 || height < 0 || scale < 1 || regionWidth < 0 || regionHeight < 0)
            {
                error = ToolResult.Error("invalid_parameter", "width/height/scale/regionWidth/regionHeight 参数不能为负数，scale 不能小于 1。", new
                {
                    width,
                    height,
                    scale,
                    regionWidth,
                    regionHeight
                });
                return false;
            }

            options = new CaptureOptions
            {
                mode = mode,
                path = path,
                format = format,
                quality = quality,
                width = width,
                height = height,
                scale = scale,
                overwrite = overwrite,
                createDirectory = createDirectory,
                allowAbsolutePath = allowAbsolutePath,
                cameraName = cameraName ?? string.Empty,
                cameraTag = cameraTag ?? string.Empty,
                includeInactive = includeInactive,
                transparent = transparent,
                windowType = windowType ?? string.Empty,
                windowTitle = windowTitle ?? string.Empty,
                includeWindowChrome = includeWindowChrome,
                x = x,
                y = y,
                regionWidth = regionWidth,
                regionHeight = regionHeight
            };
            return true;
        }

        static bool TryResolveOutputPath(CaptureOptions options, out string outputPath, out ToolResult error)
        {
            outputPath = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(options.path))
            {
                error = ToolResult.Error("missing_parameter", "缺少必需参数 'path'。", new { parameter = "path" });
                return false;
            }

            var projectRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory());
            var candidate = options.path.Trim();
            if (!Path.HasExtension(candidate))
            {
                candidate = candidate + "." + NormalizeOutputFormat(options.format);
            }

            outputPath = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(projectRoot, candidate));

            if (!options.allowAbsolutePath && !IsPathInside(outputPath, projectRoot))
            {
                error = ToolResult.Error("path_violation", "默认只允许写入项目目录内路径。若确需写入外部绝对路径，请传入 allowAbsolutePath=true。", new
                {
                    outputPath,
                    projectRoot
                });
                return false;
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = ToolResult.Error("invalid_parameter", "输出路径无效。", new { outputPath });
                return false;
            }

            if (!Directory.Exists(directory))
            {
                if (!options.createDirectory)
                {
                    error = ToolResult.Error("not_found", "输出目录不存在，且 createDirectory=false。", new { directory });
                    return false;
                }

                Directory.CreateDirectory(directory);
            }

            if (File.Exists(outputPath) && !options.overwrite)
            {
                error = ToolResult.Error("precondition_failed", "输出文件已存在，且 overwrite=false。", new { outputPath });
                return false;
            }

            return true;
        }

        static Texture2D Capture(CaptureOptions options, List<string> warnings)
        {
            switch (options.mode)
            {
                case "camera":
                    return CaptureCamera(options, warnings);
                case "game_view":
                    return CaptureGameView(options, warnings);
                case "scene_view":
                    return CaptureSceneView(options, warnings);
                case "editor_window":
                    return CaptureEditorWindow(options, warnings);
                case "unity_editor":
                    return CaptureUnityEditor(options, warnings);
                case "desktop_region":
                    return CaptureDesktopRegion(options, warnings);
                default:
                    throw new InvalidOperationException($"Unsupported mode: {options.mode}");
            }
        }

        static Texture2D CaptureCamera(CaptureOptions options, List<string> warnings)
        {
            var camera = ResolveCamera(options);
            if (camera == null)
            {
                throw new InvalidOperationException("未找到可截图的 Camera。请传入 cameraName 或 cameraTag，或确保场景中存在 Camera。");
            }

            var width = ResolveDimension(options.width, Mathf.RoundToInt(camera.pixelWidth), DefaultFallbackWidth) * options.scale;
            var height = ResolveDimension(options.height, Mathf.RoundToInt(camera.pixelHeight), DefaultFallbackHeight) * options.scale;
            var descriptor = new RenderTextureDescriptor(width, height, options.format == "exr" ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32, 24)
            {
                sRGB = options.format != "exr"
            };

            var previousTarget = camera.targetTexture;
            var previousClearFlags = camera.clearFlags;
            var previousBackground = camera.backgroundColor;
            var renderTexture = RenderTexture.GetTemporary(descriptor);
            var previousActive = RenderTexture.active;
            try
            {
                if (options.transparent)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(previousBackground.r, previousBackground.g, previousBackground.b, 0f);
                    if (options.format == "jpg" || options.format == "jpeg")
                    {
                        warnings.Add("transparent=true 对 JPG 无效，JPG 输出不保留 alpha。");
                    }
                }

                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                return ReadActiveRenderTexture(width, height, options.format == "exr");
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                camera.clearFlags = previousClearFlags;
                camera.backgroundColor = previousBackground;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static Texture2D CaptureGameView(CaptureOptions options, List<string> warnings)
        {
            if (options.includeWindowChrome)
            {
                warnings.Add("game_view includeWindowChrome=true 使用 EditorWindow 截图后端。");
                return CaptureEditorWindowRect(FindEditorWindow("GameView", string.Empty), options, warnings, "GameView");
            }

            warnings.Add("game_view 在 Unity Editor CLI 中使用 GameView 窗口捕获后端。");

            var fallback = CaptureEditorWindowRect(FindEditorWindow("GameView", string.Empty), options, warnings, "GameView");
            return options.includeWindowChrome
                ? fallback
                : CropGameViewContent(fallback, options, warnings);
        }

        static Texture2D CaptureSceneView(CaptureOptions options, List<string> warnings)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && SceneView.sceneViews.Count > 0)
            {
                sceneView = SceneView.sceneViews[0] as SceneView;
            }

            if (sceneView == null)
            {
                throw new InvalidOperationException("未找到 SceneView。");
            }

            if (options.includeWindowChrome)
            {
                return CaptureEditorWindowRect(sceneView, options, warnings, "SceneView");
            }

            var camera = sceneView.camera;
            if (camera == null)
            {
                warnings.Add("SceneView camera 不可用，改用窗口截图。");
                return CaptureEditorWindowRect(sceneView, options, warnings, "SceneView");
            }

            warnings.Add("scene_view 默认截取 SceneView camera 渲染结果，不包含工具栏、网格叠加层或选中框等 Editor chrome。");
            var width = ResolveDimension(options.width, Mathf.RoundToInt(sceneView.position.width), DefaultFallbackWidth) * options.scale;
            var height = ResolveDimension(options.height, Mathf.RoundToInt(sceneView.position.height), DefaultFallbackHeight) * options.scale;
            return RenderCameraToTexture(camera, width, height, options.format == "exr");
        }

        static Texture2D CaptureEditorWindow(CaptureOptions options, List<string> warnings)
        {
            var window = FindEditorWindow(options.windowType, options.windowTitle);
            return CaptureEditorWindowRect(window, options, warnings, string.IsNullOrWhiteSpace(options.windowType) ? "EditorWindow" : options.windowType);
        }

        static Texture2D CaptureUnityEditor(CaptureOptions options, List<string> warnings)
        {
            if (NativeScreenCapture.TryCaptureUnityEditorWindow(out var nativeTexture, out var nativeWarning))
            {
                if (!string.IsNullOrWhiteSpace(nativeWarning))
                {
                    warnings.Add(nativeWarning);
                }

                return ApplyCaptureSize(nativeTexture, options);
            }

            if (!string.IsNullOrWhiteSpace(nativeWarning))
            {
                warnings.Add(nativeWarning);
            }

            if (TryGetMainEditorWindowRect(out var rect))
            {
                return CaptureScreenRect(rect, options, warnings, "UnityEditor");
            }

            warnings.Add("未能解析 Unity 主窗口矩形，改用 focusedWindow。");
            var focused = EditorWindow.focusedWindow;
            if (focused == null)
            {
                throw new InvalidOperationException("未找到 Unity 主窗口或 focusedWindow。");
            }

            return CaptureEditorWindowRect(focused, options, warnings, focused.GetType().Name);
        }

        static Texture2D CaptureDesktopRegion(CaptureOptions options, List<string> warnings)
        {
            if (options.regionWidth <= 0 || options.regionHeight <= 0)
            {
                throw new InvalidOperationException("desktop_region 模式需要 regionWidth 和 regionHeight 大于 0。");
            }

            var rect = new Rect(options.x, options.y, options.regionWidth, options.regionHeight);
            var nativeRect = new RectInt(options.x, options.y, options.regionWidth, options.regionHeight);
            if (NativeScreenCapture.TryCaptureDesktopRect(nativeRect, out var nativeTexture, out var nativeWarning))
            {
                if (!string.IsNullOrWhiteSpace(nativeWarning))
                {
                    warnings.Add(nativeWarning);
                }

                return ApplyCaptureSize(nativeTexture, options);
            }

            if (!string.IsNullOrWhiteSpace(nativeWarning))
            {
                warnings.Add(nativeWarning);
            }

            return CaptureScreenRect(rect, options, warnings, "DesktopRegion");
        }

        static Texture2D CaptureEditorWindowRect(EditorWindow window, CaptureOptions options, List<string> warnings, string label)
        {
            if (window == null)
            {
                throw new InvalidOperationException($"未找到 EditorWindow：{label}。");
            }

            window.Focus();
            window.Repaint();
            var rect = window.position;
            if (!options.includeWindowChrome)
            {
                rect.y += 22f;
                rect.height = Mathf.Max(1f, rect.height - 22f);
            }

            if (TryCaptureEditorWindowFromNativeMainWindow(rect, options, warnings, label, out var nativeTexture))
            {
                return nativeTexture;
            }

            return CaptureScreenRect(rect, options, warnings, label);
        }

        static Texture2D CaptureScreenRect(Rect rect, CaptureOptions options, List<string> warnings, string label)
        {
            var sourceWidth = Mathf.Max(1, Mathf.RoundToInt(rect.width));
            var sourceHeight = Mathf.Max(1, Mathf.RoundToInt(rect.height));
            var captureWidth = ResolveDimension(options.width, sourceWidth, sourceWidth) * options.scale;
            var captureHeight = ResolveDimension(options.height, sourceHeight, sourceHeight) * options.scale;

            var readWidth = Mathf.Max(1, sourceWidth);
            var readHeight = Mathf.Max(1, sourceHeight);
            var colors = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(new Vector2(rect.x, rect.y), readWidth, readHeight);
            if (colors == null || colors.Length == 0)
            {
                throw new InvalidOperationException($"{label} 屏幕像素读取失败。");
            }

            var textureFormat = options.format == "exr" ? TextureFormat.RGBAFloat : TextureFormat.RGBA32;
            var texture = new Texture2D(readWidth, readHeight, textureFormat, false);
            texture.SetPixels(colors);
            texture.Apply(false, false);

            if (captureWidth != readWidth || captureHeight != readHeight)
            {
                texture = ApplyCaptureSize(texture, options);
            }

            warnings.Add($"{label} 使用 Editor 屏幕像素读取，结果可能受窗口可见性、DPI 缩放和遮挡影响。");
            return texture;
        }

        static bool TryCaptureEditorWindowFromNativeMainWindow(Rect windowRect, CaptureOptions options, List<string> warnings, string label, out Texture2D texture)
        {
            texture = null;
            if (!NativeScreenCapture.TryCaptureUnityEditorWindow(out var mainTexture, out var nativeWarning))
            {
                if (!string.IsNullOrWhiteSpace(nativeWarning))
                {
                    warnings.Add(nativeWarning);
                }

                return false;
            }

            try
            {
                if (!TryGetMainEditorWindowRect(out var unityMainRect) || unityMainRect.width <= 0f || unityMainRect.height <= 0f)
                {
                    warnings.Add($"{label} 原生窗口截图已取得，但无法解析 Unity 主窗口逻辑坐标，改用屏幕像素读取。");
                    return false;
                }

                var scaleX = mainTexture.width / unityMainRect.width;
                var scaleY = mainTexture.height / unityMainRect.height;
                var cropX = Mathf.RoundToInt((windowRect.x - unityMainRect.x) * scaleX);
                var cropY = Mathf.RoundToInt((windowRect.y - unityMainRect.y) * scaleY);
                var cropWidth = Mathf.RoundToInt(windowRect.width * scaleX);
                var cropHeight = Mathf.RoundToInt(windowRect.height * scaleY);
                var cropRect = ClampTopLeftRect(new RectInt(cropX, cropY, cropWidth, cropHeight), mainTexture.width, mainTexture.height);
                if (cropRect.width <= 0 || cropRect.height <= 0)
                {
                    warnings.Add($"{label} 原生窗口裁剪区域无效，改用屏幕像素读取。");
                    return false;
                }

                texture = CropTextureFromTopLeft(mainTexture, cropRect);
                texture = ApplyCaptureSize(texture, options);
                warnings.Add($"{label} 使用 Win32 捕获 Unity 主窗口后裁剪。");
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mainTexture);
            }
        }

        static Texture2D RenderCameraToTexture(Camera camera, int width, int height, bool hdr)
        {
            var descriptor = new RenderTextureDescriptor(width, height, hdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32, 24)
            {
                sRGB = !hdr
            };
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(descriptor);
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                return ReadActiveRenderTexture(width, height, hdr);
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static Texture2D ReadActiveRenderTexture(int width, int height, bool hdr)
        {
            var texture = new Texture2D(width, height, hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply(false, false);
            return texture;
        }

        static Texture2D ApplyCaptureSize(Texture2D texture, CaptureOptions options)
        {
            var targetWidth = ResolveDimension(options.width, texture.width, texture.width) * options.scale;
            var targetHeight = ResolveDimension(options.height, texture.height, texture.height) * options.scale;
            var resized = ResizeIfNeeded(texture, targetWidth, targetHeight);
            if (resized == null)
            {
                return texture;
            }

            UnityEngine.Object.DestroyImmediate(texture);
            return resized;
        }

        static RectInt ClampTopLeftRect(RectInt rect, int width, int height)
        {
            var x = Mathf.Clamp(rect.x, 0, width);
            var y = Mathf.Clamp(rect.y, 0, height);
            var xMax = Mathf.Clamp(rect.x + rect.width, 0, width);
            var yMax = Mathf.Clamp(rect.y + rect.height, 0, height);
            return new RectInt(x, y, Mathf.Max(0, xMax - x), Mathf.Max(0, yMax - y));
        }

        static Texture2D CropTextureFromTopLeft(Texture2D source, RectInt topLeftRect)
        {
            var bottomLeftY = source.height - topLeftRect.y - topLeftRect.height;
            var colors = source.GetPixels(topLeftRect.x, bottomLeftY, topLeftRect.width, topLeftRect.height);
            var hdr = source.format == TextureFormat.RGBAFloat || source.format == TextureFormat.RGBAHalf;
            var texture = new Texture2D(topLeftRect.width, topLeftRect.height, hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false);
            texture.SetPixels(colors);
            texture.Apply(false, false);
            return texture;
        }

        static Texture2D CropGameViewContent(Texture2D source, CaptureOptions options, List<string> warnings)
        {
            if (!TryFindGameViewContentRect(source, ResolveGameViewAspect(options), out var contentRect))
            {
                warnings.Add("GameView 内容区域自动裁剪失败，保留 EditorWindow fallback 截图。");
                return source;
            }

            var cropped = CropTextureFromTopLeft(source, contentRect);
            UnityEngine.Object.DestroyImmediate(source);
            warnings.Add("GameView 已自动裁掉 Editor tab、工具栏和黑边。");
            return ApplyCaptureSize(cropped, options);
        }

        static bool TryFindGameViewContentRect(Texture2D source, float targetAspect, out RectInt rect)
        {
            rect = default;
            var pixels = source.GetPixels32();
            if (pixels == null || pixels.Length == 0)
            {
                return false;
            }

            var rowCounts = new int[source.height];
            var columnCounts = new int[source.width];
            var ignoredTop = Mathf.Clamp(Mathf.RoundToInt(source.height * 0.09f), 0, source.height - 1);
            for (var y = ignoredTop; y < source.height; y++)
            {
                var textureRow = source.height - 1 - y;
                for (var x = 0; x < source.width; x++)
                {
                    if (!IsLikelyGamePixel(pixels[textureRow * source.width + x]))
                    {
                        continue;
                    }

                    rowCounts[y]++;
                    columnCounts[x]++;
                }
            }

            var rowThreshold = Mathf.Max(4, Mathf.RoundToInt(source.width * 0.08f));
            var columnThreshold = Mathf.Max(4, Mathf.RoundToInt(source.height * 0.08f));
            var minY = FindFirstAbove(rowCounts, rowThreshold);
            var maxY = FindLastAbove(rowCounts, rowThreshold);
            var minX = FindFirstAbove(columnCounts, columnThreshold);
            var maxX = FindLastAbove(columnCounts, columnThreshold);
            if (minX < 0 || minY < 0 || maxX <= minX || maxY <= minY)
            {
                return false;
            }

            rect = ExpandTopLeftRectToAspect(new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1), targetAspect, source.width, source.height);
            return rect.width > source.width * 0.1f && rect.height > source.height * 0.1f;
        }

        static bool IsLikelyGamePixel(Color32 color)
        {
            var max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            var min = Mathf.Min(color.r, Mathf.Min(color.g, color.b));
            if (max < 24)
            {
                return false;
            }

            var saturation = max == 0 ? 0f : (max - min) / (float)max;
            if (saturation > 0.12f && max > 35)
            {
                return true;
            }

            return color.r > 175 && color.g > 150 && color.b > 90;
        }

        static int FindFirstAbove(int[] values, int threshold)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index] >= threshold)
                {
                    return index;
                }
            }

            return -1;
        }

        static int FindLastAbove(int[] values, int threshold)
        {
            for (var index = values.Length - 1; index >= 0; index--)
            {
                if (values[index] >= threshold)
                {
                    return index;
                }
            }

            return -1;
        }

        static RectInt ExpandTopLeftRectToAspect(RectInt rect, float aspect, int width, int height)
        {
            aspect = Mathf.Max(0.01f, aspect);
            var centerX = rect.x + rect.width * 0.5f;
            var centerY = rect.y + rect.height * 0.5f;
            var targetWidth = rect.width;
            var targetHeight = rect.height;
            if (targetWidth / (float)targetHeight < aspect)
            {
                targetWidth = Mathf.CeilToInt(targetHeight * aspect);
            }
            else
            {
                targetHeight = Mathf.CeilToInt(targetWidth / aspect);
            }

            var x = Mathf.RoundToInt(centerX - targetWidth * 0.5f);
            var y = Mathf.RoundToInt(centerY - targetHeight * 0.5f);
            return ClampTopLeftRect(new RectInt(x, y, targetWidth, targetHeight), width, height);
        }

        static float ResolveGameViewAspect(CaptureOptions options)
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            var targetSizeMethod = gameViewType?.GetMethod("GetMainGameViewTargetSize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (targetSizeMethod?.Invoke(null, null) is Vector2 targetSize && targetSize.x > 0f && targetSize.y > 0f)
            {
                return targetSize.x / targetSize.y;
            }

            if (options.width > 0 && options.height > 0)
            {
                return options.width / (float)options.height;
            }

            return 1170f / 2532f;
        }

        static Texture2D ResizeIfNeeded(Texture2D source, int width, int height)
        {
            var targetWidth = width > 0 ? width : source.width;
            var targetHeight = height > 0 ? height : source.height;
            if (targetWidth == source.width && targetHeight == source.height)
            {
                return null;
            }

            var linear = source.format == TextureFormat.RGBAFloat || source.format == TextureFormat.RGBAHalf;
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, linear ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                return ReadActiveRenderTexture(targetWidth, targetHeight, linear);
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static Texture2D CloneTexture(Texture2D source, bool hdr)
        {
            var clone = new Texture2D(source.width, source.height, hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false);
            clone.SetPixels(source.GetPixels());
            clone.Apply(false, false);
            return clone;
        }

        static byte[] Encode(Texture2D texture, string format, int quality, List<string> warnings)
        {
            switch (NormalizeOutputFormat(format))
            {
                case "png":
                    return texture.EncodeToPNG();
                case "jpg":
                    return texture.EncodeToJPG(quality);
                case "tga":
                    return ImageConversion.EncodeToTGA(texture);
                case "exr":
                    if (texture.format != TextureFormat.RGBAFloat && texture.format != TextureFormat.RGBAHalf)
                    {
                        warnings.Add("EXR 输出已从当前纹理格式编码；若需要 HDR 精度，请使用 camera/scene_view 并输出 exr。");
                    }

                    return texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
                default:
                    throw new InvalidOperationException($"Unsupported format: {format}");
            }
        }

        static Camera ResolveCamera(CaptureOptions options)
        {
            var cameras = options.includeInactive
                ? Resources.FindObjectsOfTypeAll<Camera>()
                : Camera.allCameras;

            if (!string.IsNullOrWhiteSpace(options.cameraName))
            {
                var byName = cameras.FirstOrDefault(camera => string.Equals(camera.name, options.cameraName, StringComparison.Ordinal));
                if (byName != null)
                {
                    return byName;
                }
            }

            if (!string.IsNullOrWhiteSpace(options.cameraTag))
            {
                var byTag = cameras.FirstOrDefault(camera => camera != null && camera.CompareTag(options.cameraTag));
                if (byTag != null)
                {
                    return byTag;
                }
            }

            if (Camera.main != null)
            {
                return Camera.main;
            }

            return cameras.FirstOrDefault(camera => camera != null && camera.isActiveAndEnabled)
                ?? cameras.FirstOrDefault(camera => camera != null);
        }

        static EditorWindow FindEditorWindow(string windowType, string windowTitle)
        {
            var type = ResolveEditorWindowType(windowType);
            var windows = type == null
                ? Resources.FindObjectsOfTypeAll<EditorWindow>()
                : Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().ToArray();

            if (type != null && string.IsNullOrWhiteSpace(windowTitle))
            {
                var activated = EditorWindow.GetWindow(type);
                if (activated != null)
                {
                    return activated;
                }
            }

            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                var titled = windows.FirstOrDefault(window => window != null && window.titleContent != null && window.titleContent.text.IndexOf(windowTitle, StringComparison.OrdinalIgnoreCase) >= 0);
                if (titled != null)
                {
                    return titled;
                }
            }

            var existing = windows.FirstOrDefault(window => window != null);
            if (existing != null)
            {
                return existing;
            }

            return type == null ? null : EditorWindow.GetWindow(type);
        }

        static Type ResolveEditorWindowType(string windowType)
        {
            var normalized = string.IsNullOrWhiteSpace(windowType) ? "GameView" : windowType.Trim();
            switch (normalized.ToLowerInvariant())
            {
                case "gameview":
                case "game_view":
                    normalized = "GameView";
                    break;
                case "sceneview":
                case "scene_view":
                    return typeof(SceneView);
                case "inspector":
                    normalized = "InspectorWindow";
                    break;
                case "project":
                case "projectbrowser":
                    normalized = "ProjectBrowser";
                    break;
                case "hierarchy":
                case "scenehierarchy":
                    normalized = "SceneHierarchyWindow";
                    break;
                case "console":
                    normalized = "ConsoleWindow";
                    break;
            }

            return typeof(EditorWindow).Assembly.GetTypes()
                .FirstOrDefault(type => typeof(EditorWindow).IsAssignableFrom(type)
                    && (string.Equals(type.Name, normalized, StringComparison.Ordinal)
                        || string.Equals(type.FullName, normalized, StringComparison.Ordinal)));
        }

        static bool TryGetMainEditorWindowRect(out Rect rect)
        {
            rect = default;
            var containerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
            if (containerWindowType == null)
            {
                return false;
            }

            var windows = Resources.FindObjectsOfTypeAll(containerWindowType);
            var showModeProperty = containerWindowType.GetProperty("showMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var positionProperty = containerWindowType.GetProperty("position", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var window in windows)
            {
                var showMode = showModeProperty?.GetValue(window, null);
                if (showMode == null || Convert.ToInt32(showMode) != 4)
                {
                    continue;
                }

                var position = positionProperty?.GetValue(window, null);
                if (position is Rect mainRect && mainRect.width > 0f && mainRect.height > 0f)
                {
                    rect = mainRect;
                    return true;
                }
            }

            return false;
        }

        static int ResolveDimension(int requested, int source, int fallback)
        {
            if (requested > 0)
            {
                return requested;
            }

            if (source > 0)
            {
                return source;
            }

            return fallback;
        }

        static string ResolveFormat(string path, string requestedFormat)
        {
            if (!string.IsNullOrWhiteSpace(requestedFormat))
            {
                return NormalizeToken(requestedFormat);
            }

            var extension = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(extension)
                ? DefaultFormat
                : NormalizeToken(extension.TrimStart('.'));
        }

        static string NormalizeOutputFormat(string format)
        {
            var normalized = NormalizeToken(format);
            return normalized == "jpeg" ? "jpg" : normalized;
        }

        static string NormalizeToken(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        static string ResolveSourceLabel(CaptureOptions options)
        {
            switch (options.mode)
            {
                case "camera":
                    return !string.IsNullOrWhiteSpace(options.cameraName) ? options.cameraName : !string.IsNullOrWhiteSpace(options.cameraTag) ? options.cameraTag : "Camera";
                case "editor_window":
                    return string.IsNullOrWhiteSpace(options.windowType) ? "EditorWindow" : options.windowType;
                case "desktop_region":
                    return $"DesktopRegion({options.x},{options.y},{options.regionWidth},{options.regionHeight})";
                default:
                    return options.mode;
            }
        }

        static bool IsPathInside(string childPath, string parentPath)
        {
            var child = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        static class NativeScreenCapture
        {
            public static bool TryCaptureUnityEditorWindow(out Texture2D texture, out string warning)
            {
                texture = null;
                warning = string.Empty;
#if UNITY_EDITOR_WIN
                var handle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    warning = "未能取得 Unity Editor 主窗口句柄，改用 Unity 屏幕像素读取。";
                    return false;
                }

                RestoreWindow(handle);
                if (!TryGetWindowRect(handle, out var rect) || rect.Width <= 0 || rect.Height <= 0)
                {
                    warning = "未能取得 Unity Editor 主窗口尺寸，改用 Unity 屏幕像素读取。";
                    return false;
                }

                if (!TryCaptureWindow(handle, rect.Width, rect.Height, out texture))
                {
                    warning = "Win32 主窗口截图失败，改用 Unity 屏幕像素读取。";
                    return false;
                }

                warning = "UnityEditor 使用 Win32 捕获主窗口。";
                return true;
#else
                warning = "当前平台不支持 Win32 原生窗口截图，改用 Unity 屏幕像素读取。";
                return false;
#endif
            }

            public static bool TryCaptureDesktopRect(RectInt rect, out Texture2D texture, out string warning)
            {
                texture = null;
                warning = string.Empty;
#if UNITY_EDITOR_WIN
                if (rect.width <= 0 || rect.height <= 0)
                {
                    warning = "desktop_region 原生截图区域无效。";
                    return false;
                }

                if (!TryCaptureDesktop(rect, out texture))
                {
                    warning = "Win32 桌面区域截图失败，改用 Unity 屏幕像素读取。";
                    return false;
                }

                warning = "DesktopRegion 使用 Win32 捕获桌面区域。";
                return true;
#else
                warning = "当前平台不支持 Win32 桌面区域截图，改用 Unity 屏幕像素读取。";
                return false;
#endif
            }

#if UNITY_EDITOR_WIN
            const int SrcCopy = 0x00CC0020;
            const uint DibRgbColors = 0;
            const uint BiRgb = 0;

            static bool TryCaptureWindow(IntPtr windowHandle, int width, int height, out Texture2D texture)
            {
                texture = null;
                var windowDc = GetDC(windowHandle);
                if (windowDc == IntPtr.Zero)
                {
                    return false;
                }

                var memoryDc = IntPtr.Zero;
                var bitmap = IntPtr.Zero;
                var oldObject = IntPtr.Zero;
                try
                {
                    memoryDc = CreateCompatibleDC(windowDc);
                    if (memoryDc == IntPtr.Zero)
                    {
                        return false;
                    }

                    bitmap = CreateCompatibleBitmap(windowDc, width, height);
                    if (bitmap == IntPtr.Zero)
                    {
                        return false;
                    }

                    oldObject = SelectObject(memoryDc, bitmap);
                    RestoreWindow(windowHandle);
                    BringWindowToTop(windowHandle);
                    SetForegroundWindow(windowHandle);
                    System.Threading.Thread.Sleep(120);

                    var captured = false;
                    if (TryGetWindowRect(windowHandle, out var rect))
                    {
                        var desktopDc = GetDC(IntPtr.Zero);
                        try
                        {
                            captured = desktopDc != IntPtr.Zero && BitBlt(memoryDc, 0, 0, width, height, desktopDc, rect.Left, rect.Top, SrcCopy);
                        }
                        finally
                        {
                            if (desktopDc != IntPtr.Zero)
                            {
                                ReleaseDC(IntPtr.Zero, desktopDc);
                            }
                        }
                    }

                    if (!captured)
                    {
                        captured = PrintWindow(windowHandle, memoryDc, 2) || PrintWindow(windowHandle, memoryDc, 0);
                    }

                    return captured && TryCreateTextureFromBitmap(memoryDc, bitmap, width, height, out texture);
                }
                finally
                {
                    if (oldObject != IntPtr.Zero)
                    {
                        SelectObject(memoryDc, oldObject);
                    }

                    if (bitmap != IntPtr.Zero)
                    {
                        DeleteObject(bitmap);
                    }

                    if (memoryDc != IntPtr.Zero)
                    {
                        DeleteDC(memoryDc);
                    }

                    ReleaseDC(windowHandle, windowDc);
                }
            }

            static bool TryCaptureDesktop(RectInt rect, out Texture2D texture)
            {
                texture = null;
                var desktopDc = GetDC(IntPtr.Zero);
                if (desktopDc == IntPtr.Zero)
                {
                    return false;
                }

                var memoryDc = IntPtr.Zero;
                var bitmap = IntPtr.Zero;
                var oldObject = IntPtr.Zero;
                try
                {
                    memoryDc = CreateCompatibleDC(desktopDc);
                    if (memoryDc == IntPtr.Zero)
                    {
                        return false;
                    }

                    bitmap = CreateCompatibleBitmap(desktopDc, rect.width, rect.height);
                    if (bitmap == IntPtr.Zero)
                    {
                        return false;
                    }

                    oldObject = SelectObject(memoryDc, bitmap);
                    return BitBlt(memoryDc, 0, 0, rect.width, rect.height, desktopDc, rect.x, rect.y, SrcCopy)
                        && TryCreateTextureFromBitmap(memoryDc, bitmap, rect.width, rect.height, out texture);
                }
                finally
                {
                    if (oldObject != IntPtr.Zero)
                    {
                        SelectObject(memoryDc, oldObject);
                    }

                    if (bitmap != IntPtr.Zero)
                    {
                        DeleteObject(bitmap);
                    }

                    if (memoryDc != IntPtr.Zero)
                    {
                        DeleteDC(memoryDc);
                    }

                    ReleaseDC(IntPtr.Zero, desktopDc);
                }
            }

            static bool TryCreateTextureFromBitmap(IntPtr deviceContext, IntPtr bitmap, int width, int height, out Texture2D texture)
            {
                texture = null;
                var info = new BitmapInfo();
                info.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
                info.Header.Width = width;
                info.Header.Height = -height;
                info.Header.Planes = 1;
                info.Header.BitCount = 32;
                info.Header.Compression = BiRgb;
                info.Header.SizeImage = (uint)(width * height * 4);

                var buffer = new byte[width * height * 4];
                var lines = GetDIBits(deviceContext, bitmap, 0, (uint)height, buffer, ref info, DibRgbColors);
                if (lines == 0)
                {
                    return false;
                }

                var pixels = new Color32[width * height];
                for (var y = 0; y < height; y++)
                {
                    var sourceRow = y * width * 4;
                    var targetRow = (height - 1 - y) * width;
                    for (var x = 0; x < width; x++)
                    {
                        var sourceIndex = sourceRow + x * 4;
                        pixels[targetRow + x] = new Color32(
                            buffer[sourceIndex + 2],
                            buffer[sourceIndex + 1],
                            buffer[sourceIndex],
                            255);
                    }
                }

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return true;
            }

            static bool TryGetWindowRect(IntPtr handle, out WinRect rect)
            {
                return GetWindowRect(handle, out rect);
            }

            static void RestoreWindow(IntPtr handle)
            {
                ShowWindow(handle, 9);
                var currentThread = GetCurrentThreadId();
                var foregroundWindow = GetForegroundWindow();
                var foregroundThread = foregroundWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
                var targetThread = GetWindowThreadProcessId(handle, out _);
                var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
                var attachedTarget = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
                try
                {
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
                    SetActiveWindow(handle);
                    SetFocus(handle);
                    BringWindowToTop(handle);
                    SetForegroundWindow(handle);
                }
                finally
                {
                    if (attachedTarget)
                    {
                        AttachThreadInput(currentThread, targetThread, false);
                    }

                    if (attachedForeground)
                    {
                        AttachThreadInput(currentThread, foregroundThread, false);
                    }
                }

                BringWindowToTop(handle);
                SetForegroundWindow(handle);
                System.Threading.Thread.Sleep(250);
            }

            [StructLayout(LayoutKind.Sequential)]
            struct WinRect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;

                public int Width => Right - Left;
                public int Height => Bottom - Top;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct BitmapInfoHeader
            {
                public uint Size;
                public int Width;
                public int Height;
                public ushort Planes;
                public ushort BitCount;
                public uint Compression;
                public uint SizeImage;
                public int XPelsPerMeter;
                public int YPelsPerMeter;
                public uint ClrUsed;
                public uint ClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct BitmapInfo
            {
                public BitmapInfoHeader Header;
                public uint Colors;
            }

            [DllImport("user32.dll")]
            static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("user32.dll")]
            static extern bool GetWindowRect(IntPtr hWnd, out WinRect lpRect);

            [DllImport("user32.dll")]
            static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

            [DllImport("user32.dll")]
            static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern bool BringWindowToTop(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            static extern IntPtr GetForegroundWindow();

            [DllImport("kernel32.dll")]
            static extern uint GetCurrentThreadId();

            [DllImport("user32.dll")]
            static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll")]
            static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

            [DllImport("user32.dll")]
            static extern IntPtr SetActiveWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern IntPtr SetFocus(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

            [DllImport("gdi32.dll")]
            static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

            [DllImport("gdi32.dll")]
            static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [DllImport("gdi32.dll")]
            static extern bool DeleteObject(IntPtr ho);

            [DllImport("gdi32.dll")]
            static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

            [DllImport("gdi32.dll")]
            static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BitmapInfo lpbmi, uint usage);
#endif
        }

        sealed class CaptureOptions
        {
            public string mode;
            public string path;
            public string format;
            public int quality;
            public int width;
            public int height;
            public int scale;
            public bool overwrite;
            public bool createDirectory;
            public bool allowAbsolutePath;
            public string cameraName;
            public string cameraTag;
            public bool includeInactive;
            public bool transparent;
            public string windowType;
            public string windowTitle;
            public bool includeWindowChrome;
            public int x;
            public int y;
            public int regionWidth;
            public int regionHeight;
        }
    }
}
