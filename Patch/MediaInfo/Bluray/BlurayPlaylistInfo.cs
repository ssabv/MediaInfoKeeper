using System.Collections.Generic;

namespace MediaInfoKeeper.Patch.MediaInfo.Bluray
{
    internal sealed class BlurayPlaylistInfo
    {
        public string PlaylistName { get; set; }

        public double TotalLengthSeconds { get; set; }

        public int VideoStreamCount { get; set; }

        public int AudioStreamCount { get; set; }

        public int SubtitleStreamCount { get; set; }

        public List<double> Chapters { get; } = new List<double>();

        public List<string> ClipFileNames { get; } = new List<string>();

        public List<BlurayPlaylistStream> Streams { get; } = new List<BlurayPlaylistStream>();
    }

    internal sealed class BlurayPlaylistStream
    {
        public int Order { get; set; }

        public int Pid { get; set; }

        public byte StreamType { get; set; }

        public string Codec { get; set; }

        public string LanguageCode { get; set; }

        public string LanguageName => BlurayLanguageCodes.GetName(LanguageCode);

        public bool IsVideo { get; set; }

        public bool IsAudio { get; set; }

        public bool IsSubtitle { get; set; }
    }
}
