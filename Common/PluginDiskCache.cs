using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace MediaInfoKeeper.Common
{
    internal static class PluginDiskCache
    {
        public static T GetJson<T>(string scope, string key, TimeSpan maxAge, JsonSerializerOptions options)
        {
            var path = GetCacheFilePath(scope, key, ".json");
            if (!IsFresh(path, maxAge))
            {
                return default;
            }

            try
            {
                var body = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(body, options);
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Debug("磁盘缓存读取失败: scope={0}, key={1}, msg={2}", scope, key, ex.Message);
                return default;
            }
        }

        public static void SetJson<T>(string scope, string key, T value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                return;
            }

            var path = GetCacheFilePath(scope, key, ".json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(value, options));
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Debug("磁盘缓存写入失败: scope={0}, key={1}, msg={2}", scope, key, ex.Message);
            }
        }

        public static byte[] GetBytes(string scope, string key, TimeSpan maxAge, string extension)
        {
            var path = GetCacheFilePath(scope, key, extension);
            if (!IsFresh(path, maxAge))
            {
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                return bytes == null || bytes.Length == 0 ? null : bytes;
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Debug("磁盘缓存读取失败: scope={0}, key={1}, msg={2}", scope, key, ex.Message);
                return null;
            }
        }

        public static void SetBytes(string scope, string key, byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            var path = GetCacheFilePath(scope, key, extension);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Debug("磁盘缓存写入失败: scope={0}, key={1}, msg={2}", scope, key, ex.Message);
            }
        }

        public static void Remove(string scope, string key, string extension)
        {
            var path = GetCacheFilePath(scope, key, extension);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Debug("磁盘缓存删除失败: scope={0}, key={1}, msg={2}", scope, key, ex.Message);
            }
        }

        private static bool IsFresh(string path, TimeSpan maxAge)
        {
            return File.Exists(path) &&
                   DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) <= maxAge;
        }

        private static string GetCacheFilePath(string scope, string key, string extension)
        {
            var cachePath = Plugin.Instance?.AppHost?.Resolve<IApplicationPaths>()?.CachePath;
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                throw new InvalidOperationException("Emby CachePath is not available.");
            }

            return Path.Combine(cachePath, Plugin.PluginName, scope, HashKey(key) + NormalizeExtension(extension));
        }

        private static string HashKey(string key)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".cache";
            }

            return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
        }
    }
}
