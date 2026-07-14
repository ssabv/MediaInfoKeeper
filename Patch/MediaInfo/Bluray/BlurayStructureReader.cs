using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscUtils.Udf;

namespace MediaInfoKeeper.Patch.MediaInfo.Bluray
{
    internal static class BlurayStructureReader
    {
        private const double MainPlaylistDurationToleranceSeconds = 3d;
        private const double MainPlaylistMovieMinSeconds = 20 * 60d;
        private const double MainPlaylistMovieMaxSeconds = 4 * 60 * 60d;

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

        internal static BlurayPlaylistInfo ReadMainPlaylistFromFileSystem(dynamic fileSystem, double? preferredDurationSeconds = null)
        {
            return ReadFromFileSystem(fileSystem, preferredDurationSeconds);
        }

        internal static List<string> ResolvePlaylistClipPaths(dynamic fileSystem, BlurayPlaylistInfo playlist)
        {
            var clipNames = (playlist?.ClipFileNames ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            if (clipNames.Count == 0)
            {
                return new List<string>();
            }

            var streamDir = ResolveStreamDirectory(fileSystem);
            if (streamDir == null)
            {
                return new List<string>();
            }

            var resolved = new List<string>();
            foreach (var clipName in clipNames)
            {
                var candidate = CombineFileSystemPath(streamDir, clipName);
                try
                {
                    var info = fileSystem.GetFileInfo(candidate);
                    if (info.Exists)
                    {
                        resolved.Add(candidate);
                    }
                }
                catch
                {
                    // Continue with the remaining clips; a playlist can reference optional angles.
                }
            }

            return resolved;
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
            if (udf == null)
            {
                return null;
            }

            using (udf)
            {
                return ReadFromFileSystem(udf, preferredDurationSeconds);
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

            var candidateScore = GetMainPlaylistStreamScore(candidate);
            var bestScore = GetMainPlaylistStreamScore(currentBest);
            var candidateLooksLikeMovie = LooksLikeMoviePlaylist(candidate);
            var bestLooksLikeMovie = LooksLikeMoviePlaylist(currentBest);

            if (candidateLooksLikeMovie != bestLooksLikeMovie)
            {
                return candidateLooksLikeMovie;
            }

            if (candidateScore != bestScore)
            {
                return candidateScore > bestScore;
            }

            var durationDelta = candidate.TotalLengthSeconds - currentBest.TotalLengthSeconds;
            if (durationDelta > MainPlaylistDurationToleranceSeconds)
            {
                return true;
            }

            if (durationDelta > 0)
            {
                return true;
            }

            return false;
        }

        private static int GetMainPlaylistStreamScore(BlurayPlaylistInfo playlist)
        {
            if (playlist == null)
            {
                return 0;
            }

            return (playlist.SubtitleStreamCount * 100) + (playlist.AudioStreamCount * 10) + playlist.VideoStreamCount;
        }

        private static bool LooksLikeMoviePlaylist(BlurayPlaylistInfo playlist)
        {
            if (playlist == null)
            {
                return false;
            }

            return playlist.TotalLengthSeconds >= MainPlaylistMovieMinSeconds &&
                   playlist.TotalLengthSeconds <= MainPlaylistMovieMaxSeconds;
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
                    if (fileSystem.DirectoryExists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try the next path shape.
                }
            }

            return null;
        }

        private static string ResolveStreamDirectory(dynamic fileSystem)
        {
            var candidates = new[]
            {
                "BDMV/STREAM",
                "/BDMV/STREAM",
                "BDMV\\STREAM",
                "\\BDMV\\STREAM"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (fileSystem.DirectoryExists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try the next path shape.
                }
            }

            return null;
        }

        private static string CombineFileSystemPath(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return fileName;
            }

            var separator = directory.Contains("\\", StringComparison.Ordinal) ? "\\" : "/";
            return directory.TrimEnd('\\', '/') + separator + fileName;
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
