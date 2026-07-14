using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 解析 NFO 人物节点时提取 thumb 地址并补充人物图片链接。
    /// </summary>
    public static class NfoMetadataEnhance
    {
        private static readonly object InitLock = new object();
        private static readonly AsyncLocal<string> PersonContent = new AsyncLocal<string>();

        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            ValidationType = ValidationType.None,
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true
        };

        private static readonly XmlWriterSettings WriterSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = false
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isHookInstalled;
        private static bool waitingForAssembly;
        private static Assembly nfoMetadataAssembly;

        public static bool IsReady => harmony != null && (waitingForAssembly || isHookInstalled);

        public static bool IsWaiting => waitingForAssembly && !isHookInstalled;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock)
            {
                harmony ??= new Harmony("mediainfokeeper.nfometadataenhance");

                if (isHookInstalled)
                {
                    return;
                }

                if (TryGetLoadedAssembly("NfoMetadata", out var assembly))
                {
                    TryInstallHooks(assembly);
                    return;
                }

                if (!waitingForAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    waitingForAssembly = true;
                    PatchLog.Waiting(logger, nameof(NfoMetadataEnhance), "NfoMetadata", isEnabled);
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var assembly = args?.LoadedAssembly;
            if (assembly == null)
            {
                return;
            }

            if (!string.Equals(assembly.GetName().Name, "NfoMetadata", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (InitLock)
            {
                if (isHookInstalled)
                {
                    return;
                }

                TryInstallHooks(assembly);
                if (isHookInstalled)
                {
                    waitingForAssembly = false;
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }
            }
        }

        private static bool TryGetLoadedAssembly(string assemblyName, out Assembly assembly)
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            return assembly != null;
        }

        private static void TryInstallHooks(Assembly assembly)
        {
            try
            {
                nfoMetadataAssembly = assembly;
                var version = assembly.GetName().Version;
                var parserGeneric = assembly.GetType("NfoMetadata.Parsers.BaseNfoParser`1", false);
                if (parserGeneric == null)
                {
                    PatchLog.InitFailed(logger, nameof(NfoMetadataEnhance), "BaseNfoParser`1 未找到");
                    return;
                }

                var patchedMethods = new List<MethodInfo>();
                foreach (var itemType in GetNfoItemTypes())
                {
                    var parserType = parserGeneric.MakeGenericType(itemType);
                    var getPersonFromXmlNode = PatchMethodResolver.Resolve(
                        parserType,
                        version,
                        new MethodSignatureProfile
                        {
                            Name = $"base-nfo-parser-{itemType.Name}-getpersonfromxmlnode",
                            MethodName = "GetPersonFromXmlNode",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[] { typeof(XmlReader) },
                            ReturnType = typeof(Task<PersonInfo>)
                        },
                        logger,
                        $"NfoMetadataEnhance.BaseNfoParser<{itemType.FullName}>.GetPersonFromXmlNode");

                    if (getPersonFromXmlNode == null)
                    {
                        continue;
                    }

                    harmony.Patch(
                        getPersonFromXmlNode,
                        prefix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(GetPersonFromXmlNodePrefix)),
                        postfix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(GetPersonFromXmlNodePostfix)));

                    patchedMethods.Add(getPersonFromXmlNode);
                    PatchLog.Patched(logger, nameof(NfoMetadataEnhance), getPersonFromXmlNode);
                }

                if (patchedMethods.Count == 0)
                {
                    PatchLog.InitFailed(logger, nameof(NfoMetadataEnhance), "GetPersonFromXmlNode 目标方法缺失");
                    return;
                }

                isHookInstalled = true;
                waitingForAssembly = false;
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(NfoMetadataEnhance), ex.Message);
                logger?.Error("NfoMetadataEnhance 初始化失败。");
                logger?.Error(ex.ToString());
            }
        }

        private static Type[] GetNfoItemTypes()
        {
            return new[]
            {
                typeof(Video),
                typeof(Episode),
                typeof(Series),
                typeof(Season),
                typeof(BoxSet),
                typeof(Game),
                typeof(Person)
            };
        }

        [HarmonyPrefix]
        private static bool GetPersonFromXmlNodePrefix(ref XmlReader reader)
        {
            if (!isEnabled)
            {
                PersonContent.Value = null;
                return true;
            }

            try
            {
                var content = ReadCurrentNodeContent(reader);
                PersonContent.Value = content;
                if (content != null)
                {
                    reader = XmlReader.Create(new StringReader(content), ReaderSettings);
                }
            }
            catch (Exception ex)
            {
                PersonContent.Value = null;
                logger?.Debug("NfoMetadataEnhance 读取人物节点失败: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void GetPersonFromXmlNodePostfix(Task<PersonInfo> __result)
        {
            if (!isEnabled || __result == null)
            {
                PersonContent.Value = null;
                return;
            }

            var personContent = PersonContent.Value;
            PersonContent.Value = null;

            if (string.IsNullOrWhiteSpace(personContent))
            {
                return;
            }

            _ = Task.Run(async () => await SetImageUrlAsync(__result, personContent).ConfigureAwait(false));
        }

        private static string ReadCurrentNodeContent(XmlReader reader)
        {
            if (reader == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
            using (var xmlWriter = XmlWriter.Create(writer, WriterSettings))
            {
                while (reader.Read())
                {
                    xmlWriter.WriteNode(reader, true);
                    if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        break;
                    }
                }
            }

            return sb.ToString();
        }

        private static async Task SetImageUrlAsync(Task<PersonInfo> personInfoTask, string personContent)
        {
            try
            {
                var personInfo = await personInfoTask.ConfigureAwait(false);
                if (personInfo == null || string.IsNullOrWhiteSpace(personContent))
                {
                    return;
                }

                using (var reader = XmlReader.Create(new StringReader(personContent), ReaderSettings))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (!reader.IsStartElement("thumb"))
                        {
                            continue;
                        }

                        var thumb = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (IsValidHttpUrl(thumb))
                        {
                            personInfo.ImageUrl = thumb;
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("NfoMetadataEnhance 设置人物图片失败: {0}", ex.Message);
            }
        }

        private static bool IsValidHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
