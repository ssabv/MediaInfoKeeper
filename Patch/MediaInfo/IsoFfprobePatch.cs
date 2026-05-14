using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    internal static class IsoFfprobeRouting
    {
        public static bool HasCustomFfprobePath()
        {
            var options = Plugin.Instance?.Options?.GetMediaInfoOptions();
            return !string.IsNullOrWhiteSpace(options?.CustomFfprobePath);
        }

        public static bool IsDiscProbeInput(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return false;
            }

            var normalized = inputPath.Replace('\\', '/');
            if (normalized.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalized.IndexOf("/BDMV/index.bdmv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (normalized.IndexOf("/BDMV/STREAM/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (normalized.IndexOf("/VIDEO_TS/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (normalized.EndsWith("/index.bdmv", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalized.EndsWith("/video_ts.ifo", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 为 ISO/BDMV 探测放开 Emby 原版的 IsSupported 硬拦截，
    /// 让自定义 ffprobe 替换逻辑有机会接管真实探测流程。
    /// </summary>
    public static class IsoProbeSupport
    {
        private static Harmony harmony;
        private static MethodInfo isSupportedMethod;
        private static ILogger logger;

        public static bool IsReady => harmony != null && isSupportedMethod != null;

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            if (!enabled)
            {
                return;
            }

            try
            {
                var embyProvidersAssembly = Assembly.Load("Emby.Providers");
                var mediaBrowserModelAssembly = Assembly.Load("MediaBrowser.Model");
                var ffProbeVideoInfoType = embyProvidersAssembly?.GetType("Emby.Providers.MediaInfo.FFProbeVideoInfo");
                var mediaProtocolType = mediaBrowserModelAssembly?.GetType("MediaBrowser.Model.MediaInfo.MediaProtocol");

                if (ffProbeVideoInfoType == null || mediaProtocolType == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoProbeSupport), "关键运行时类型缺失");
                    return;
                }

                isSupportedMethod = PatchMethodResolver.Resolve(
                    ffProbeVideoInfoType,
                    embyProvidersAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "ffprobevideoinfo-issupported-exact",
                        MethodName = "IsSupported",
                        BindingFlags = BindingFlags.Static | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(string), mediaProtocolType },
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "IsoProbeSupport.FFProbeVideoInfo.IsSupported");

                if (isSupportedMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoProbeSupport), "未命中 IsSupported");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.iso-probe-support");
                PatchLog.Patched(logger, nameof(IsoProbeSupport), isSupportedMethod);
                harmony.Patch(
                    isSupportedMethod,
                    postfix: new HarmonyMethod(typeof(IsoProbeSupport), nameof(IsSupportedPostfix)));
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(IsoProbeSupport), ex.Message);
                logger?.Error("IsoProbeSupport 初始化异常：{0}", ex);
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            // 一次性安装，运行期直接读取配置。
        }

        private static void IsSupportedPostfix(string __0, ref bool __result)
        {
            if (__result)
            {
                return;
            }

            if (!IsoFfprobeRouting.HasCustomFfprobePath())
            {
                return;
            }

            if (!IsoFfprobeRouting.IsDiscProbeInput(__0))
            {
                return;
            }

            __result = true;
            logger?.Debug("ISO/BDMV 探测放行：path={0}", __0 ?? "<null>");
        }
    }

    /// <summary>
    /// 当 ffprobe 输入命中 ISO/BDMV 挂载内容时，切换为专用 ffprobe 可执行文件。
    /// </summary>
    public static class IsoFfprobePath
    {
        private static readonly Regex InputArgumentRegex = new Regex(
            @"-i\s+(file:""[^""]+""|""[^""]+""|\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FileProtocolRegex = new Regex(
            "^file:\"(?<path>[^\"]+)\"$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Harmony harmony;
        private static MethodInfo onBeforeStartProcess;
        private static FieldInfo runTypeField;
        private static Type ffRunType;
        private static object ffprobeEnumValue;
        private static ILogger logger;

        public static bool IsReady => harmony != null && onBeforeStartProcess != null && runTypeField != null && ffprobeEnumValue != null;

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            if (!enabled)
            {
                return;
            }

            try
            {
                var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
                var processRunAssembly = Assembly.Load("Emby.ProcessRun");
                var ffRunnerBaseType = mediaEncodingAssembly?.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunnerBase");
                var startParamsType = processRunAssembly?.GetType("Emby.ProcessRun.Common.StartParams");
                ffRunType = mediaEncodingAssembly?.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");

                if (ffRunnerBaseType == null || startParamsType == null || ffRunType == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoFfprobePath), "关键运行时类型缺失");
                    return;
                }

                runTypeField = ffRunnerBaseType.GetField("runType", BindingFlags.Instance | BindingFlags.NonPublic);
                if (runTypeField == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoFfprobePath), "未找到 FfRunnerBase.runType");
                    return;
                }

                ffprobeEnumValue = Enum.Parse(ffRunType, "Ffprobe");
                onBeforeStartProcess = PatchMethodResolver.Resolve(
                    ffRunnerBaseType,
                    mediaEncodingAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "ffrunnerbase-onbeforestartprocess-exact",
                        MethodName = "OnBeforeStartProcess",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        ParameterTypes = new[] { startParamsType },
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "IsoFfprobePath.FfRunnerBase.OnBeforeStartProcess");

                if (onBeforeStartProcess == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoFfprobePath), "未命中 OnBeforeStartProcess");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.iso-ffprobe-path");
                PatchLog.Patched(logger, nameof(IsoFfprobePath), onBeforeStartProcess);
                harmony.Patch(
                    onBeforeStartProcess,
                    prefix: new HarmonyMethod(typeof(IsoFfprobePath), nameof(OnBeforeStartProcessPrefix)));
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(IsoFfprobePath), ex.Message);
                logger?.Error("IsoFfprobePath 初始化异常：{0}", ex);
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            // 一次性安装，运行期直接读取配置。
        }

        private static void OnBeforeStartProcessPrefix(object __instance, object __0)
        {
            if (__instance == null || __0 == null || !IsoFfprobeRouting.HasCustomFfprobePath())
            {
                return;
            }

            if (!IsFfprobeRun(__instance))
            {
                return;
            }

            NormalizeSpecialProtocolInput(__0);

            var inputPath = ExtractInputPath(__0);
            if (!IsoFfprobeRouting.IsDiscProbeInput(inputPath))
            {
                logger?.Debug("ISO/BDMV ffprobe 未命中挂载内容：input={0}", inputPath ?? "<unknown>");
                return;
            }

            var customPath = Plugin.Instance?.Options?.GetMediaInfoOptions()?.CustomFfprobePath?.Trim();
            if (string.IsNullOrWhiteSpace(customPath))
            {
                return;
            }

            if (!File.Exists(customPath))
            {
                logger?.Warn("ISO/BDMV 专用 ffprobe 不存在：{0}", customPath);
                return;
            }

            var startParamsType = __0.GetType();
            var fileNameProperty = startParamsType.GetProperty("FileName", BindingFlags.Instance | BindingFlags.Public);
            var workingDirectoryProperty = startParamsType.GetProperty("WorkingDirectory", BindingFlags.Instance | BindingFlags.Public);
            if (fileNameProperty == null)
            {
                logger?.Warn("ISO/BDMV 专用 ffprobe 切换失败：StartParams.FileName 不可用");
                return;
            }

            var originalFileName = fileNameProperty.GetValue(__0) as string;
            if (string.Equals(originalFileName, customPath, StringComparison.Ordinal))
            {
                return;
            }

            fileNameProperty.SetValue(__0, customPath);

            var workingDirectory = Path.GetDirectoryName(customPath);
            if (workingDirectoryProperty != null && !string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectoryProperty.SetValue(__0, workingDirectory);
            }

            logger?.Debug(
                "ISO/BDMV ffprobe 切换：input={0} original={1} custom={2}",
                inputPath ?? "<unknown>",
                originalFileName ?? "<unknown>",
                customPath);
        }

        private static void NormalizeSpecialProtocolInput(object startParams)
        {
            try
            {
                var argumentsProperty = startParams.GetType().GetProperty("Arguments", BindingFlags.Instance | BindingFlags.Public);
                var arguments = argumentsProperty?.GetValue(startParams) as string;
                if (string.IsNullOrWhiteSpace(arguments))
                {
                    return;
                }

                const string wrongPrefix = "-i file:\"bluray:";
                var index = arguments.IndexOf(wrongPrefix, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return;
                }

                var fixedArguments = arguments.Replace("-i file:\"bluray:", "-i \"bluray:", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(fixedArguments, arguments, StringComparison.Ordinal))
                {
                    return;
                }

                argumentsProperty.SetValue(startParams, fixedArguments);
                logger?.Debug("ISO/BDMV ffprobe 输入修正：{0}", fixedArguments);
            }
            catch (Exception ex)
            {
                logger?.Debug("IsoFfprobePath.NormalizeSpecialProtocolInput failed: {0}", ex.Message);
            }
        }

        private static bool IsFfprobeRun(object instance)
        {
            try
            {
                var runTypeValue = runTypeField?.GetValue(instance);
                return runTypeValue != null && Equals(runTypeValue, ffprobeEnumValue);
            }
            catch (Exception ex)
            {
                logger?.Debug("IsoFfprobePath.IsFfprobeRun failed: {0}", ex.Message);
                return false;
            }
        }

        private static string ExtractInputPath(object startParams)
        {
            try
            {
                var argumentsProperty = startParams.GetType().GetProperty("Arguments", BindingFlags.Instance | BindingFlags.Public);
                var arguments = argumentsProperty?.GetValue(startParams) as string;
                if (string.IsNullOrWhiteSpace(arguments))
                {
                    return null;
                }

                var match = InputArgumentRegex.Match(arguments);
                if (!match.Success)
                {
                    return null;
                }

                var token = match.Groups[1].Value?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                var fileProtocolMatch = FileProtocolRegex.Match(token);
                if (fileProtocolMatch.Success)
                {
                    return fileProtocolMatch.Groups["path"].Value;
                }

                if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
                {
                    return token.Substring(1, token.Length - 2);
                }

                return token;
            }
            catch (Exception ex)
            {
                logger?.Debug("IsoFfprobePath.ExtractInputPath failed: {0}", ex.Message);
                return null;
            }
        }
    }
}
