using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MediaInfoKeeper.Patch.MediaInfo.Bluray
{
    internal static class BlurayMplsParser
    {
        public static BlurayPlaylistInfo Parse(string playlistName, byte[] data)
        {
            if (data == null || data.Length < 64)
            {
                return null;
            }

            var pos = 0;
            var fileType = ReadString(data, 8, ref pos);
            if (!string.Equals(fileType, "MPLS0100", StringComparison.Ordinal) &&
                !string.Equals(fileType, "MPLS0200", StringComparison.Ordinal) &&
                !string.Equals(fileType, "MPLS0300", StringComparison.Ordinal))
            {
                return null;
            }

            var playlistOffset = ReadInt32(data, ref pos);
            var chapterOffset = ReadInt32(data, ref pos);
            _ = ReadInt32(data, ref pos);

            var result = new BlurayPlaylistInfo
            {
                PlaylistName = playlistName
            };

            var playlistStreams = new Dictionary<int, BlurayPlaylistStream>();
            var streamOrder = 0;
            var clipLengths = new List<ClipTiming>();

            pos = playlistOffset;
            _ = ReadInt32(data, ref pos);
            _ = ReadInt16(data, ref pos);
            var clipCount = ReadInt16(data, ref pos);
            _ = ReadInt16(data, ref pos);

            for (var i = 0; i < clipCount; i++)
            {
                var blockStart = pos;
                var blockLength = ReadInt16(data, ref pos);
                var clipFileName = ReadString(data, 5, ref pos);
                var clipFileType = ReadString(data, 4, ref pos);
                pos++;
                var multiAngle = (data[pos] >> 4) & 1;
                pos += 2;

                if (!string.IsNullOrWhiteSpace(clipFileName) && !string.IsNullOrWhiteSpace(clipFileType))
                {
                    result.ClipFileNames.Add(clipFileName + "." + clipFileType.ToLowerInvariant());
                }

                var rawTimeIn = NormalizeTimestamp(ReadInt32(data, ref pos));
                var rawTimeOut = NormalizeTimestamp(ReadInt32(data, ref pos));
                var timeIn = rawTimeIn / 45000.0;
                var timeOut = rawTimeOut / 45000.0;
                var length = Math.Max(0, timeOut - timeIn);

                clipLengths.Add(new ClipTiming
                {
                    TimeIn = timeIn,
                    RelativeTimeIn = result.TotalLengthSeconds,
                    RelativeTimeOut = result.TotalLengthSeconds + length
                });
                result.TotalLengthSeconds += length;

                pos += 12;

                if (multiAngle > 0)
                {
                    var angleCount = data[pos];
                    pos += 2;
                    for (var j = 0; j < angleCount - 1; j++)
                    {
                        var angleClipFileName = ReadString(data, 5, ref pos);
                        var angleClipFileType = ReadString(data, 4, ref pos);
                        if (!string.IsNullOrWhiteSpace(angleClipFileName) && !string.IsNullOrWhiteSpace(angleClipFileType))
                        {
                            result.ClipFileNames.Add(angleClipFileName + "." + angleClipFileType.ToLowerInvariant());
                        }

                        pos++;
                    }
                }

                _ = ReadInt16(data, ref pos);
                pos += 2;
                var primaryVideoStreams = data[pos++];
                var primaryAudioStreams = data[pos++];
                var pgStreams = data[pos++];
                var igStreams = data[pos++];
                var secondaryAudioStreams = data[pos++];
                var secondaryVideoStreams = data[pos++];
                pos++;
                pos += 5;

                ReadStreams(data, ref pos, primaryVideoStreams, playlistStreams, ref streamOrder);
                ReadStreams(data, ref pos, primaryAudioStreams, playlistStreams, ref streamOrder);
                ReadStreams(data, ref pos, pgStreams, playlistStreams, ref streamOrder);
                ReadStreams(data, ref pos, igStreams, playlistStreams, ref streamOrder);

                for (var j = 0; j < secondaryAudioStreams; j++)
                {
                    ReadStreams(data, ref pos, 1, playlistStreams, ref streamOrder);
                    pos += 2;
                }

                for (var j = 0; j < secondaryVideoStreams; j++)
                {
                    ReadStreams(data, ref pos, 1, playlistStreams, ref streamOrder);
                    pos += 6;
                }

                pos += blockLength - (pos - blockStart) + 2;
            }

            pos = chapterOffset + 4;
            var chapterCount = ReadInt16(data, ref pos);
            for (var i = 0; i < chapterCount; i++)
            {
                if (data[pos + 1] == 1)
                {
                    var clipIndex = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
                    var chapterTimestamp = NormalizeTimestamp(unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4))));
                    if (clipIndex >= 0 && clipIndex < clipLengths.Count)
                    {
                        var clip = clipLengths[clipIndex];
                        var absoluteSeconds = chapterTimestamp / 45000.0;
                        var relativeSeconds = absoluteSeconds - clip.TimeIn + clip.RelativeTimeIn;
                        if (result.TotalLengthSeconds - relativeSeconds > 1.0)
                        {
                            result.Chapters.Add(relativeSeconds);
                        }
                    }
                }

                pos += 14;
            }

            result.Streams.AddRange(playlistStreams.Values.OrderBy(stream => stream.Order));
            result.VideoStreamCount = result.Streams.Count(stream => stream.IsVideo);
            result.AudioStreamCount = result.Streams.Count(stream => stream.IsAudio);
            result.SubtitleStreamCount = result.Streams.Count(stream => stream.IsSubtitle);
            return result;
        }

        private static void ReadStreams(
            byte[] data,
            ref int pos,
            int count,
            Dictionary<int, BlurayPlaylistStream> streams,
            ref int streamOrder)
        {
            for (var i = 0; i < count; i++)
            {
                var stream = CreatePlaylistStream(data, ref pos);
                if (stream != null)
                {
                    stream.Order = streamOrder++;
                    streams[stream.Pid] = stream;
                }
            }
        }

        private static BlurayPlaylistStream CreatePlaylistStream(byte[] data, ref int pos)
        {
            BlurayPlaylistStream stream = null;

            var streamEntryLength = data[pos++];
            var streamEntryStart = pos;
            var streamType = data[pos++];
            var pid = 0;

            switch (streamType)
            {
                case 1:
                    pid = ReadInt16(data, ref pos);
                    break;
                case 2:
                    pos += 2;
                    pid = ReadInt16(data, ref pos);
                    break;
                case 3:
                    pos++;
                    pid = ReadInt16(data, ref pos);
                    break;
                case 4:
                    pos += 2;
                    pid = ReadInt16(data, ref pos);
                    break;
            }

            pos = streamEntryStart + streamEntryLength;

            var streamAttributesLength = data[pos++];
            var streamAttributesStart = pos;
            var codecType = data[pos++];

            switch (codecType)
            {
                case 1:
                case 2:
                case 27:
                case 32:
                case 36:
                case 234:
                    stream = new BlurayPlaylistStream
                    {
                        Pid = pid,
                        StreamType = codecType,
                        Codec = GetCodec(codecType),
                        IsVideo = true
                    };
                    break;
                case 3:
                case 4:
                case 128:
                case 129:
                case 131:
                case 132:
                case 133:
                case 134:
                case 161:
                case 162:
                    pos++;
                    stream = new BlurayPlaylistStream
                    {
                        Pid = pid,
                        StreamType = codecType,
                        Codec = GetCodec(codecType),
                        LanguageCode = ReadString(data, 3, ref pos),
                        IsAudio = true
                    };
                    break;
                case 144:
                case 145:
                    stream = new BlurayPlaylistStream
                    {
                        Pid = pid,
                        StreamType = codecType,
                        Codec = "pgs",
                        LanguageCode = ReadString(data, 3, ref pos),
                        IsSubtitle = true
                    };
                    break;
                case 146:
                    pos++;
                    stream = new BlurayPlaylistStream
                    {
                        Pid = pid,
                        StreamType = codecType,
                        Codec = "subtitle",
                        LanguageCode = ReadString(data, 3, ref pos),
                        IsSubtitle = true
                    };
                    break;
            }

            pos = streamAttributesStart + streamAttributesLength;
            return stream;
        }

        private static string GetCodec(byte streamType)
        {
            return streamType switch
            {
                1 => "mpeg1video",
                2 => "mpeg2video",
                27 => "h264",
                32 => "mvc",
                36 => "hevc",
                234 => "vc1",
                3 => "mp1",
                4 => "mp2",
                128 => "lpcm",
                129 => "ac3",
                131 => "truehd",
                132 => "eac3",
                133 => "dtshd",
                134 => "dtshd_ma",
                161 => "eac3",
                162 => "dtshd_secondary",
                144 => "pgs",
                145 => "ig",
                146 => "text",
                _ => "unknown"
            };
        }

        private static int NormalizeTimestamp(int value)
        {
            if (value < 0)
            {
                value &= 0x7FFFFFFF;
            }

            return value;
        }

        private static short ReadInt16(byte[] data, ref int pos)
        {
            var value = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pos));
            pos += 2;
            return value;
        }

        private static int ReadInt32(byte[] data, ref int pos)
        {
            var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            pos += 4;
            return value;
        }

        private static string ReadString(byte[] data, int count, ref int pos)
        {
            var value = Encoding.ASCII.GetString(data, pos, count);
            pos += count;
            return value;
        }

        private sealed class ClipTiming
        {
            public double TimeIn { get; set; }

            public double RelativeTimeIn { get; set; }

            public double RelativeTimeOut { get; set; }
        }
    }
}
