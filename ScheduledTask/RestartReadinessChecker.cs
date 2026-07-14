using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace MediaInfoKeeper.ScheduledTask
{
    internal static class RestartReadinessChecker
    {
        public static RestartReadinessStatus GetStatus(
            ISessionManager sessionManager,
            ILiveTvManager liveTvManager,
            ILogger logger)
        {
            var playbackUserCount = CountPlaybackUsers(sessionManager?.Sessions);
            var hasActiveRecording = false;

            try
            {
                hasActiveRecording = liveTvManager?.HasActiveRecording == true;
            }
            catch (Exception ex)
            {
                logger?.ErrorException("检查 Live TV 录制状态失败，将不把录制状态作为本次重启阻止条件。", ex);
            }

            return new RestartReadinessStatus(playbackUserCount, hasActiveRecording);
        }

        private static int CountPlaybackUsers(IEnumerable<SessionInfo> sessions)
        {
            if (sessions == null)
            {
                return 0;
            }

            var userIds = new HashSet<long>();
            foreach (var session in sessions)
            {
                if (session?.HasUser != true || session.NowPlayingItem == null)
                {
                    continue;
                }

                userIds.Add(session.UserInternalId);
                foreach (var additionalUser in session.AdditionalUsers ?? Array.Empty<SessionUserInfo>())
                {
                    if (additionalUser.UserInternalId != 0)
                    {
                        userIds.Add(additionalUser.UserInternalId);
                    }
                }
            }

            return userIds.Count;
        }
    }

    internal sealed class RestartReadinessStatus
    {
        public RestartReadinessStatus(int playbackUserCount, bool hasActiveRecording)
        {
            PlaybackUserCount = playbackUserCount;
            HasActiveRecording = hasActiveRecording;
        }

        public int PlaybackUserCount { get; }

        public bool HasActiveRecording { get; }

        public bool CanRestart => PlaybackUserCount == 0 && !HasActiveRecording;

        public string Describe()
        {
            if (PlaybackUserCount > 0 && HasActiveRecording)
            {
                return $"检测到 {PlaybackUserCount} 个正在播放的用户，且 Live TV 正在录制";
            }

            if (PlaybackUserCount > 0)
            {
                return $"检测到 {PlaybackUserCount} 个正在播放的用户";
            }

            if (HasActiveRecording)
            {
                return "检测到 Live TV 正在录制";
            }

            return "当前没有用户正在播放，也没有 Live TV 正在录制";
        }
    }
}
