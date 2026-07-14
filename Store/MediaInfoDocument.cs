using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Store
{
    public class MediaInfoDocument
    {
        [Flags]
        public enum MediaInfoRestoreResult
        {
            // 恢复成功
            Restored = 1,
            // 已经存在，无需恢复
            AlreadyExists = 0,
            // 恢复失败
            Failed = -1
        }

        private const string MediaInfoFileExtension = "-mediainfo.json";
        private static readonly Regex TemplateTokenRegex = new Regex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
        public MediaSourceInfo MediaSourceInfo { get; set; }

        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();

        public string EmbeddedImage { get; set; }

        public EmbeddedInfoSnapshot EmbeddedInfo { get; set; }

        public bool HasPayload()
        {
            return MediaSourceInfo != null ||
                   (Chapters?.Count ?? 0) > 0 ||
                   !string.IsNullOrWhiteSpace(EmbeddedImage) ||
                   EmbeddedInfo != null;
        }

        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            return BuildSidecarPath(item, MediaInfoFileExtension);
        }

        private static string BuildSidecarPath(BaseItem item, string extension)
        {
            var jsonRootFolder = Plugin.Instance.Options.MediaInfo.MediaInfoJsonRootFolder?.Trim();
            var fileName = item.FileNameWithoutExtension + extension;

            if (string.IsNullOrWhiteSpace(jsonRootFolder))
            {
                return Path.Combine(item.ContainingFolderPath, fileName);
            }

            if (ContainsTemplateToken(jsonRootFolder))
            {
                return BuildTemplatePath(item, jsonRootFolder, fileName);
            }

            return Path.Combine(GetConfiguredRootFolder(item, jsonRootFolder), fileName);
        }

        private static string GetConfiguredRootFolder(BaseItem item, string jsonRootFolder)
        {
            if (item is Audio)
            {
                return Path.Combine(jsonRootFolder, "music");
            }

            return jsonRootFolder;
        }

        private static bool ContainsTemplateToken(string template)
        {
            if (string.IsNullOrEmpty(template))
            {
                return false;
            }

            foreach (Match match in TemplateTokenRegex.Matches(template))
            {
                if (IsSupportedTemplateToken(match.Groups[1].Value))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildTemplatePath(BaseItem item, string template, string fileName)
        {
            var expandedPath = ExpandPathTemplate(item, template, fileName)?.Trim();
            if (string.IsNullOrWhiteSpace(expandedPath))
            {
                return Path.Combine(item.ContainingFolderPath, fileName);
            }

            return LooksLikeJsonFilePath(expandedPath)
                ? expandedPath
                : Path.Combine(expandedPath, fileName);
        }

        private static string ExpandPathTemplate(BaseItem item, string template, string fileName)
        {
            return TemplateTokenRegex.Replace(template, match =>
            {
                var token = match.Groups[1].Value;
                var value = ResolveTemplateValue(item, token);
                return NormalizeTemplateValueBoundary(template, match, value ?? match.Value);
            });
        }

        private static string NormalizeTemplateValueBoundary(string template, Match match, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var previousChar = match.Index > 0 ? template[match.Index - 1] : '\0';
            var nextIndex = match.Index + match.Length;
            var nextChar = nextIndex < template.Length ? template[nextIndex] : '\0';

            if (IsDirectorySeparator(previousChar))
            {
                value = TrimPathRoot(value);
            }

            if (IsDirectorySeparator(nextChar))
            {
                value = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
            }

            return value;
        }

        private static string TrimPathRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var rootedPath = TrimWindowsPathRoot(path);
            var root = Path.GetPathRoot(rootedPath);
            if (!string.IsNullOrEmpty(root) && rootedPath.StartsWith(root, StringComparison.Ordinal))
            {
                rootedPath = rootedPath.Substring(root.Length);
            }

            return rootedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
        }

        private static string TrimWindowsPathRoot(string path)
        {
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                return path.Substring(2);
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            {
                var separatorCount = 0;
                for (var i = 2; i < path.Length; i++)
                {
                    if (!IsDirectorySeparator(path[i]))
                    {
                        continue;
                    }

                    separatorCount++;
                    if (separatorCount == 2)
                    {
                        return path.Substring(i + 1);
                    }
                }
            }

            return path;
        }

        private static bool IsDirectorySeparator(char value)
        {
            return value == Path.DirectorySeparatorChar ||
                   value == Path.AltDirectorySeparatorChar ||
                   value == '/' ||
                   value == '\\';
        }

        private static bool IsSupportedTemplateToken(string token)
        {
            switch (token ?? string.Empty)
            {
                case "parentFolderPath":
                case "fileNameWithoutExtension":
                case "extension":
                case "type":
                case "tmdbId":
                case "mediaType":
                    return true;
                default:
                    return false;
            }
        }

        private static string ResolveTemplateValue(BaseItem item, string token)
        {
            switch (token ?? string.Empty)
            {
                case "parentFolderPath":
                    return item.ContainingFolderPath ?? string.Empty;
                case "fileNameWithoutExtension":
                    return item.FileNameWithoutExtension ?? string.Empty;
                case "extension":
                    return Path.GetExtension(item.Path ?? item.FileName) ?? string.Empty;
                case "type":
                    return GetTemplateType(item);
                case "tmdbId":
                    return item.GetProviderId(MetadataProviders.Tmdb)?.Trim() ?? string.Empty;
                case "mediaType":
                    return item is Audio ? "music" : "video";
                default:
                    return null;
            }
        }

        private static bool LooksLikeJsonFilePath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTemplateType(BaseItem item)
        {
            if (item is Movie)
            {
                return "movie";
            }

            if (item is Series || item is Season || item is Episode)
            {
                return "tv";
            }

            return string.Empty;
        }

        public static void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            DeleteSidecar(item, directoryService, GetMediaInfoJsonPath(item), "JSON", source);
        }

        private static void DeleteSidecar(BaseItem item, IDirectoryService directoryService, string path, string label, string source)
        {
            var logger = Plugin.SharedLogger;
            var fileSystem = Plugin.FileSystem;
            var file = directoryService.GetFile(path);

            if (file?.Exists is not true)
            {
                logger?.Info($"MediaInfoKeeper {source} 未找到{label}: {item.FileName ?? item.Path} {path}");
                return;
            }

            try
            {
                logger?.Info($"MediaInfoKeeper {source} 尝试删除{label}: {item.FileName ?? item.Path} {path}");
                fileSystem.DeleteFile(path);
            }
            catch (Exception e)
            {
                logger?.Error(e.Message);
                logger?.Debug(e.StackTrace);
            }
        }
    }

    public class EmbeddedInfoSnapshot
    {
        public string Name { get; set; }

        public string Album { get; set; }

        public string[] AlbumArtists { get; set; } = Array.Empty<string>();

        public string[] Artists { get; set; } = Array.Empty<string>();

        public string[] Genres { get; set; } = Array.Empty<string>();

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public int? ProductionYear { get; set; }

        public Dictionary<string, string> ProviderIds { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

}
