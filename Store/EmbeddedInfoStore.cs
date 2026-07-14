using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Store
{
    public class EmbeddedInfoStore
    {
        private const string DefaultImageMimeType = "image/jpeg";
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public EmbeddedInfoStore(IJsonSerializer jsonSerializer)
        {
            this.jsonSerializer = jsonSerializer;
            this.logger = Plugin.SharedLogger;
        }

        public EmbeddedInfoSnapshot ReadFromFile(BaseItem item)
        {
            var snapshot = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault()?.EmbeddedInfo;
            this.logger.Debug($"EmbeddedInfoStore 从文件读取音乐元数据: {(item.FileName ?? item.Path)} 是否存在={snapshot != null}");
            return snapshot;
        }

        public bool HasInFile(BaseItem item)
        {
            var hasInFile = ReadFromFile(item) != null;
            this.logger.Debug($"EmbeddedInfoStore 检查文件是否包含音乐元数据: {(item.FileName ?? item.Path)} 结果={hasInFile}");
            return hasInFile;
        }

        public bool WriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            if (document.EmbeddedInfo != null)
            {
                this.logger.Debug($"EmbeddedInfoStore Json写入音乐元数据跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            var snapshot = CreateForPersist(item);
            if (snapshot == null)
            {
                this.logger.Debug($"EmbeddedInfoStore Json写入音乐元数据跳过: {(item.FileName ?? item.Path)} 非音频或无有效数据");
                return false;
            }

            document.EmbeddedInfo = snapshot;
            document.EmbeddedImage = GetPrimaryImageBase64(item as Audio);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"EmbeddedInfoStore Json写入音乐元数据成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public void OverWriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            document.EmbeddedInfo = CreateForPersist(item);
            var embeddedImage = GetPrimaryImageBase64(item as Audio);
            if (!string.IsNullOrWhiteSpace(embeddedImage))
            {
                document.EmbeddedImage = embeddedImage;
            }
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"EmbeddedInfoStore 写入音乐元数据成功: {(item.FileName ?? item.Path)}");
        }

        public void OverWriteImageToFile(BaseItem item)
        {
            if (item is not Audio audio)
            {
                return;
            }

            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            var embeddedImage = GetPrimaryImageBase64(audio);
            if (string.IsNullOrWhiteSpace(embeddedImage))
            {
                return;
            }

            document.EmbeddedImage = embeddedImage;
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"EmbeddedInfoStore 写入音乐主图成功: {(item.FileName ?? item.Path)}");
        }

        public MediaInfoDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item)
        {
            if (item is not Audio audio)
            {
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            var snapshot = ReadFromFile(item);
            if (snapshot == null)
            {
                this.logger.Debug($"EmbeddedInfoStore 恢复音乐元数据失败: {(item.FileName ?? item.Path)} JSON 中无音乐元数据");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(snapshot.Name))
                {
                    audio.Name = snapshot.Name;
                }

                audio.Album = snapshot.Album;
                audio.AlbumArtists = snapshot.AlbumArtists ?? Array.Empty<string>();
                audio.Artists = snapshot.Artists ?? Array.Empty<string>();
                audio.Genres = snapshot.Genres ?? Array.Empty<string>();
                audio.IndexNumber = snapshot.IndexNumber;
                audio.ParentIndexNumber = snapshot.ParentIndexNumber;
                audio.ProductionYear = snapshot.ProductionYear;
                audio.SetProviderIds(new ProviderIdDictionary(snapshot.ProviderIds ?? new Dictionary<string, string>()));
                RestorePrimaryImage(audio, item);
                audio.UpdateToRepository(ItemUpdateType.MetadataImport);

                this.logger.Debug($"EmbeddedInfoStore 恢复音乐元数据完成: {(item.FileName ?? item.Path)}");
                return MediaInfoDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e)
            {
                this.logger.Error($"EmbeddedInfoStore 恢复音乐元数据失败: {(item.FileName ?? item.Path)}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }
        }

        private EmbeddedInfoSnapshot CreateForPersist(BaseItem item)
        {
            if (item is not Audio audio)
            {
                return null;
            }

            return new EmbeddedInfoSnapshot
            {
                Name = audio.Name,
                Album = audio.Album,
                AlbumArtists = audio.AlbumArtists ?? Array.Empty<string>(),
                Artists = audio.Artists ?? Array.Empty<string>(),
                Genres = audio.Genres ?? Array.Empty<string>(),
                IndexNumber = audio.IndexNumber,
                ParentIndexNumber = audio.ParentIndexNumber,
                ProductionYear = audio.ProductionYear,
                ProviderIds = new Dictionary<string, string>(audio.ProviderIds ?? new ProviderIdDictionary(), StringComparer.OrdinalIgnoreCase)
            };
        }

        private string GetPrimaryImageBase64(Audio audio)
        {
            if (audio == null)
            {
                return null;
            }

            var primaryImage = audio.GetImageInfo(ImageType.Primary, 0);
            var imagePath = primaryImage?.Path;
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                this.logger.Debug($"EmbeddedInfoStore 主图写入跳过: {(audio.FileName ?? audio.Path)} 无可读取主图");
                return null;
            }

            try
            {
                var base64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
                this.logger.Debug($"EmbeddedInfoStore 主图写入成功: {(audio.FileName ?? audio.Path)}");
                return base64;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"EmbeddedInfoStore 主图写入失败: {(audio.FileName ?? audio.Path)} {ex.Message}");
                return null;
            }
        }

        private void RestorePrimaryImage(Audio audio, BaseItem item)
        {
            if (audio.HasImage(ImageType.Primary))
            {
                return;
            }

            var document = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(document?.EmbeddedImage))
            {
                return;
            }

            try
            {
                var imageBytes = Convert.FromBase64String(document.EmbeddedImage);
                var mimeType = DetectImageMimeType(imageBytes);
                using var imageStream = new MemoryStream(imageBytes, writable: false);
                var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(audio);
                Plugin.ProviderManager.SaveImage(
                        audio,
                        libraryOptions,
                        imageStream,
                        mimeType.AsMemory(),
                        ImageType.Primary,
                        null,
                        Array.Empty<long>(),
                        new DirectoryService(this.logger, Plugin.FileSystem),
                        updateImageCache: true,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                this.logger.Info($"EmbeddedInfoStore 主图恢复成功: {(audio.FileName ?? audio.Path)}");
            }
            catch (Exception ex)
            {
                this.logger.Warn($"EmbeddedInfoStore 主图恢复失败: {(audio.FileName ?? audio.Path)} {ex.Message}");
            }
        }

        private static string DetectImageMimeType(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length < 4)
            {
                return DefaultImageMimeType;
            }

            if (imageBytes.Length >= 3 &&
                imageBytes[0] == 0xFF &&
                imageBytes[1] == 0xD8 &&
                imageBytes[2] == 0xFF)
            {
                return DefaultImageMimeType;
            }

            if (imageBytes.Length >= 8 &&
                imageBytes[0] == 0x89 &&
                imageBytes[1] == 0x50 &&
                imageBytes[2] == 0x4E &&
                imageBytes[3] == 0x47 &&
                imageBytes[4] == 0x0D &&
                imageBytes[5] == 0x0A &&
                imageBytes[6] == 0x1A &&
                imageBytes[7] == 0x0A)
            {
                return "image/png";
            }

            if (imageBytes.Length >= 6 &&
                imageBytes[0] == 0x47 &&
                imageBytes[1] == 0x49 &&
                imageBytes[2] == 0x46)
            {
                return "image/gif";
            }

            if (imageBytes.Length >= 2 &&
                imageBytes[0] == 0x42 &&
                imageBytes[1] == 0x4D)
            {
                return "image/bmp";
            }

            if (imageBytes.Length >= 12 &&
                imageBytes[0] == 0x52 &&
                imageBytes[1] == 0x49 &&
                imageBytes[2] == 0x46 &&
                imageBytes[3] == 0x46 &&
                imageBytes[8] == 0x57 &&
                imageBytes[9] == 0x45 &&
                imageBytes[10] == 0x42 &&
                imageBytes[11] == 0x50)
            {
                return "image/webp";
            }

            return DefaultImageMimeType;
        }

        private void SaveDocuments(List<MediaInfoDocument> documents, MediaInfoDocument document, string mediaInfoJsonPath)
        {
            if (documents.Count == 0)
            {
                documents.Add(document);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(mediaInfoJsonPath));
            this.jsonSerializer.SerializeToFile(documents, mediaInfoJsonPath);
        }

        private List<MediaInfoDocument> ReadDocuments(string mediaInfoJsonPath)
        {
            try
            {
                return this.jsonSerializer.DeserializeFromFile<List<MediaInfoDocument>>(mediaInfoJsonPath) ??
                       new List<MediaInfoDocument>();
            }
            catch
            {
                return new List<MediaInfoDocument>();
            }
        }
    }
}
