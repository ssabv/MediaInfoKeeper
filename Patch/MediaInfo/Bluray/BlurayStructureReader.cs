using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace MediaInfoKeeper.Patch.MediaInfo.Bluray
{
    internal static class BlurayStructureReader
    {
        private static MediaBrowser.Model.Logging.ILogger Logger => MediaInfoKeeper.Plugin.SharedLogger;
        private const double MainPlaylistDurationToleranceSeconds = 3d;

        public static BlurayPlaylistInfo ReadMainPlaylist(string path, double? preferredDurationSeconds = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                return ReadFromIso(path, preferredDurationSeconds);
            }

            return ReadFromDirectory(path, preferredDurationSeconds);
        }

        private static void LogPlaylist(BlurayPlaylistInfo playlist)
        {
            Logger?.Debug(
                "BlurayStructureReader: playlist={0} length={1:F3}s video={2} audio={3} subtitle={4}",
                playlist?.PlaylistName ?? "<null>",
                playlist?.TotalLengthSeconds ?? 0,
                playlist?.VideoStreamCount ?? 0,
                playlist?.AudioStreamCount ?? 0,
                playlist?.SubtitleStreamCount ?? 0);
        }

        private static BlurayPlaylistInfo ReadFromDirectory(string path, double? preferredDurationSeconds)
        {
            var discRoot = NormalizeDirectoryRoot(path);
            if (string.IsNullOrWhiteSpace(discRoot))
            {
                return null;
            }

            var playlistDir = Path.Combine(discRoot, "BDMV", "PLAYLIST");
            if (!Directory.Exists(playlistDir))
            {
                return null;
            }

            BlurayPlaylistInfo best = null;
            foreach (var file in Directory.EnumerateFiles(playlistDir, "*.mpls", SearchOption.TopDirectoryOnly))
            {
                var bytes = File.ReadAllBytes(file);
                var playlist = BlurayMplsParser.Parse(Path.GetFileName(file), bytes);
                if (playlist == null)
                {
                    continue;
                }

                LogPlaylist(playlist);
                if (IsBetterMainPlaylist(playlist, best, preferredDurationSeconds))
                {
                    best = playlist;
                }
            }

            return best;
        }

        private static BlurayPlaylistInfo ReadFromIso(string path, double? preferredDurationSeconds)
        {
            using var stream = File.OpenRead(path);
            var udf = OpenUdf(stream);
            if (udf != null)
            {
                Logger?.Debug("BlurayStructureReader: UDF 打开成功 path={0}", path);
                var result = ReadFromFileSystem(udf, preferredDurationSeconds);
                if (result != null)
                {
                    return result;
                }
            }
            else
            {
                Logger?.Debug("BlurayStructureReader: UDF 打开失败 path={0}", path);
            }

            try
            {
                stream.Position = 0;
                var cd = new CDReader(stream, true, true);
                Logger?.Debug("BlurayStructureReader: ISO9660 打开成功 path={0}", path);
                return ReadFromFileSystem(cd, preferredDurationSeconds);
            }
            catch
            {
                Logger?.Debug("BlurayStructureReader: ISO9660 打开失败 path={0}", path);
                return null;
            }
        }

        private static BlurayPlaylistInfo ReadFromFileSystem(dynamic fileSystem, double? preferredDurationSeconds)
        {
            var playlistDir = ResolvePlaylistDirectory(fileSystem);
            if (playlistDir == null)
            {
                return null;
            }

            BlurayPlaylistInfo best = null;
            foreach (var file in fileSystem.GetFiles(playlistDir, "*.mpls", SearchOption.TopDirectoryOnly))
            {
                using var input = fileSystem.OpenFile(file, FileMode.Open, FileAccess.Read);
                using var ms = new MemoryStream();
                input.CopyTo(ms);
                var playlist = BlurayMplsParser.Parse(Path.GetFileName(file), ms.ToArray());
                if (playlist == null)
                {
                    continue;
                }

                LogPlaylist(playlist);
                if (IsBetterMainPlaylist(playlist, best, preferredDurationSeconds))
                {
                    best = playlist;
                }
            }

            return best;
        }

        private static bool IsBetterMainPlaylist(BlurayPlaylistInfo candidate, BlurayPlaylistInfo currentBest, double? preferredDurationSeconds)
        {
            if (candidate == null)
            {
                return false;
            }

            if (currentBest == null)
            {
                return true;
            }

            if (preferredDurationSeconds.HasValue)
            {
                var candidateDelta = Math.Abs(candidate.TotalLengthSeconds - preferredDurationSeconds.Value);
                var currentDelta = Math.Abs(currentBest.TotalLengthSeconds - preferredDurationSeconds.Value);
                if (Math.Abs(candidateDelta - currentDelta) > MainPlaylistDurationToleranceSeconds)
                {
                    return candidateDelta < currentDelta;
                }
            }

            var durationDelta = candidate.TotalLengthSeconds - currentBest.TotalLengthSeconds;
            if (durationDelta > MainPlaylistDurationToleranceSeconds)
            {
                return true;
            }

            if (Math.Abs(durationDelta) <= MainPlaylistDurationToleranceSeconds)
            {
                var candidateScore = (candidate.SubtitleStreamCount * 100) + (candidate.AudioStreamCount * 10) + candidate.VideoStreamCount;
                var bestScore = (currentBest.SubtitleStreamCount * 100) + (currentBest.AudioStreamCount * 10) + currentBest.VideoStreamCount;
                if (candidateScore != bestScore)
                {
                    return candidateScore > bestScore;
                }
            }

            if (durationDelta > 0)
            {
                return true;
            }

            return false;
        }

        private static string ResolvePlaylistDirectory(dynamic fileSystem)
        {
            var candidates = new[]
            {
                "BDMV/PLAYLIST",
                "/BDMV/PLAYLIST",
                "BDMV\\PLAYLIST",
                "\\BDMV\\PLAYLIST",
                "PLAYLIST",
                "/PLAYLIST"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var exists = fileSystem.DirectoryExists(candidate);
                    Logger?.Debug("BlurayStructureReader: DirectoryExists path={0} exists={1}", candidate, exists);
                    if (exists)
                    {
                        return candidate;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Debug("BlurayStructureReader: DirectoryExists 异常 path={0} error={1}", candidate, ex.Message);
                }
            }

            try
            {
                var rootDirs = fileSystem.GetDirectories("/", "*", SearchOption.TopDirectoryOnly);
                Logger?.Debug("BlurayStructureReader: root dirs={0}", string.Join("|", rootDirs));
            }
            catch (Exception ex)
            {
                Logger?.Debug("BlurayStructureReader: 枚举根目录失败 error={0}", ex.Message);
            }

            return null;
        }

        private static UdfReader OpenUdf(Stream stream)
        {
            try
            {
                if (stream == null || !stream.CanSeek)
                {
                    return null;
                }

                stream.Position = 0;
                if (!UdfReader.Detect(stream))
                {
                    stream.Position = 0;
                    return null;
                }

                stream.Position = 0;
                return new UdfReader(stream, 2048);
            }
            catch
            {
                if (stream?.CanSeek == true)
                {
                    stream.Position = 0;
                }

                return null;
            }
        }

        private static string NormalizeDirectoryRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/');
            var index = normalized.IndexOf("/BDMV/", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return path.Substring(0, index);
            }

            if (normalized.EndsWith("/BDMV", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(0, path.Length - 5);
            }

            return Directory.Exists(Path.Combine(path, "BDMV")) ? path : null;
        }
    }
}
