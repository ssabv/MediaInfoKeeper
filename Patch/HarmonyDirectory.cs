using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    internal static class HarmonyDirectory
    {
        private static readonly object InitLock = new object();
        private static bool initialized;

        public static void Initialize(ILogger logger)
        {
            lock (InitLock)
            {
                if (initialized)
                {
                    return;
                }

                initialized = true;

                try
                {
                    var tempDirectory = Path.GetTempPath();
                    var appHost = Plugin.Instance?.AppHost;
                    var applicationPaths = appHost?.Resolve<IApplicationPaths>();
                    if (!string.IsNullOrWhiteSpace(applicationPaths?.TempDirectory))
                    {
                        tempDirectory = applicationPaths.TempDirectory;
                    }

                    if (string.IsNullOrWhiteSpace(tempDirectory))
                    {
                        return;
                    }

                    Directory.CreateDirectory(tempDirectory);
                    var probePath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllBytes(probePath, new byte[] { 0 });
                    File.Delete(probePath);

                    var switchesType = FindMonoModSwitchesType();
                    SetHarmonyDumpDirectory(switchesType, tempDirectory, logger);
                    var actualTempDirectory = GetMonoModSwitchValue(switchesType, "HelperDropPath");
                    if (string.IsNullOrWhiteSpace(actualTempDirectory))
                    {
                        logger?.Warn((Plugin.Instance?.Name ?? Plugin.PluginName) + " Harmony 临时目录设置失败：MonoMod HelperDropPath 未设置。");
                        return;
                    }

                    logger?.Info((Plugin.Instance?.Name ?? Plugin.PluginName) + " Harmony 临时目录设置为: " + actualTempDirectory);
                }
                catch (Exception e)
                {
                    logger?.Warn("Harmony 临时目录设置失败。");
                    logger?.Warn(e.ToString());
                }
            }
        }

        private static Type FindMonoModSwitchesType()
        {
            var switchesType = typeof(Harmony).Assembly.GetType("MonoMod.Switches", throwOnError: false);
            if (switchesType != null)
            {
                return switchesType;
            }

            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("MonoMod.Switches", throwOnError: false))
                .FirstOrDefault(type => type != null);
        }

        private static void SetHarmonyDumpDirectory(Type switchesType, string tempDirectory, ILogger logger)
        {
            var setSwitchValue = switchesType?.GetMethod(
                "SetSwitchValue",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(string), typeof(object) },
                null);
            if (setSwitchValue == null)
            {
                logger?.Warn((Plugin.Instance?.Name ?? Plugin.PluginName) + " Harmony 临时目录设置失败：未找到 MonoMod.Switches.SetSwitchValue。");
                return;
            }

            setSwitchValue.Invoke(null, new object[] { "DMDDumpTo", tempDirectory });
            setSwitchValue.Invoke(null, new object[] { "HelperDropPath", tempDirectory });
        }

        private static string GetMonoModSwitchValue(Type switchesType, string switchName)
        {
            var tryGetSwitchValue = switchesType?.GetMethod(
                "TryGetSwitchValue",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(string), typeof(object).MakeByRefType() },
                null);
            if (tryGetSwitchValue == null)
            {
                return null;
            }

            var args = new object[] { switchName, null };
            var found = tryGetSwitchValue.Invoke(null, args) as bool?;
            return found == true ? args[1]?.ToString() : null;
        }
    }
}
