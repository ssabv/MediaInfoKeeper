using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 当歌曲或音乐专辑缺少主图时，优先使用专辑/子歌曲可用封面作为 DTO 与图片接口回退。
    /// </summary>
    public static class AudioAlbumPrimaryFallback
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo getBaseItemDtoInternal;
        private static MethodInfo getImageCacheTag;
        private static MethodInfo getImage;
        private static Type imageRequestType;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enableAudioAlbumPrimaryFallback)
        {
            lock (InitLock)
            {
                logger = pluginLogger;
                isEnabled = enableAudioAlbumPrimaryFallback;

                if (harmony != null)
                {
                    Configure(enableAudioAlbumPrimaryFallback);
                    return;
                }

                try
                {
                    var implementationAssembly = Assembly.Load("Emby.Server.Implementations");
                    var implementationVersion = implementationAssembly?.GetName().Version;
                    var dtoServiceType = implementationAssembly?.GetType("Emby.Server.Implementations.Dto.DtoService", false);
                    var apiAssembly = Assembly.Load("Emby.Api");
                    var apiVersion = apiAssembly?.GetName().Version;
                    var imageServiceType = apiAssembly?.GetType("Emby.Api.Images.ImageService", false);
                    imageRequestType = apiAssembly?.GetType("Emby.Api.Images.ImageRequest", false);
                    if (dtoServiceType == null)
                    {
                        PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), "未找到 DtoService");
                        return;
                    }
                    if (imageServiceType == null || imageRequestType == null)
                    {
                        PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), "未找到 ImageService/ImageRequest");
                        return;
                    }

                    getBaseItemDtoInternal = PatchMethodResolver.Resolve(
                        dtoServiceType,
                        implementationVersion,
                        new MethodSignatureProfile
                        {
                            Name = "dtoservice-getbaseitemdtointernal",
                            MethodName = "GetBaseItemDtoInternal",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[]
                            {
                                typeof(BaseItem),
                                typeof(DtoOptions),
                                typeof(User),
                                typeof(CancellationToken)
                            },
                            ReturnType = typeof(BaseItemDto)
                        },
                        logger,
                        "AudioAlbumPrimaryFallback.DtoService.GetBaseItemDtoInternal");

                    getImageCacheTag = PatchMethodResolver.Resolve(
                        dtoServiceType,
                        implementationVersion,
                        new MethodSignatureProfile
                        {
                            Name = "dtoservice-getimagecachetag-itemimageinfo",
                            MethodName = "GetImageCacheTag",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[]
                            {
                                typeof(BaseItem),
                                typeof(ItemImageInfo)
                            },
                            ReturnType = typeof(string)
                        },
                        logger,
                        "AudioAlbumPrimaryFallback.DtoService.GetImageCacheTag");

                    getImage = PatchMethodResolver.Resolve(
                        imageServiceType,
                        apiVersion,
                        new MethodSignatureProfile
                        {
                            Name = "imageservice-getimage",
                            MethodName = "GetImage",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                            IsStatic = false,
                            ParameterTypes = new[]
                            {
                                imageRequestType,
                                typeof(long),
                                typeof(BaseItem),
                                typeof(bool)
                            },
                            ReturnType = typeof(Task<object>)
                        },
                        logger,
                        "AudioAlbumPrimaryFallback.ImageService.GetImage");

                    if (getBaseItemDtoInternal == null || getImageCacheTag == null || getImage == null)
                    {
                        PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), "DTO 图片相关方法缺失");
                        return;
                    }

                    harmony = new Harmony("mediainfokeeper.audioalbumprimaryfallback");
                    PatchLog.Patched(logger, nameof(AudioAlbumPrimaryFallback), getBaseItemDtoInternal);

                    if (isEnabled)
                    {
                        Patch();
                    }
                }
                catch (Exception ex)
                {
                    PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), ex.Message);
                    logger?.Error("AudioAlbumPrimaryFallback 初始化异常：{0}", ex);
                    harmony = null;
                    isEnabled = false;
                }
            }
        }

        public static void Configure(bool enableAudioAlbumPrimaryFallback)
        {
            lock (InitLock)
            {
                isEnabled = enableAudioAlbumPrimaryFallback;
                if (harmony == null)
                {
                    return;
                }

                if (isEnabled)
                {
                    Patch();
                }
                else
                {
                    Unpatch();
                }
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || getBaseItemDtoInternal == null)
            {
                return;
            }

            harmony.Patch(
                getBaseItemDtoInternal,
                postfix: new HarmonyMethod(typeof(AudioAlbumPrimaryFallback), nameof(GetBaseItemDtoInternalPostfix)));
            harmony.Patch(
                getImage,
                prefix: new HarmonyMethod(typeof(AudioAlbumPrimaryFallback), nameof(GetImagePrefix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || getBaseItemDtoInternal == null)
            {
                return;
            }

            harmony.Unpatch(getBaseItemDtoInternal, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(getImage, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtoInternalPostfix(
            object __instance,
            BaseItem item,
            User user,
            ref BaseItemDto __result)
        {
            if (!isEnabled || __instance == null || item == null || __result == null)
            {
                return;
            }

            if (item is Audio audio)
            {
                ApplyAudioPrimaryFallback(__instance, audio, user, __result);
                return;
            }

            if (item is MusicAlbum musicAlbum)
            {
                ApplyMusicAlbumPrimaryFallback(__instance, musicAlbum, user, __result);
            }
        }

        private static void ApplyAudioPrimaryFallback(
            object dtoService,
            Audio audio,
            User user,
            BaseItemDto dto)
        {
            if (audio.GetImageInfo(ImageType.Primary, 0) != null)
            {
                return;
            }

            var displayParentId = audio.ImageDisplayParentId;
            if (displayParentId == 0 || displayParentId == audio.InternalId)
            {
                return;
            }

            var displayParent = Plugin.LibraryManager?.GetItemById(displayParentId);
            if (!TryResolvePrimaryImageSource(displayParent, user, out var imageOwner, out var imageInfo))
            {
                return;
            }

            try
            {
                var displayParentPrimaryTag = getImageCacheTag?.Invoke(
                    dtoService,
                    new object[] { imageOwner, imageInfo }) as string;
                if (string.IsNullOrWhiteSpace(displayParentPrimaryTag))
                {
                    return;
                }

                dto.PrimaryImageItemId = imageOwner.GetClientId();
                dto.PrimaryImageTag = displayParentPrimaryTag;
            }
            catch (Exception ex)
            {
                logger?.Debug("AudioAlbumPrimaryFallback failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static void GetImagePrefix(
            object __0,
            ref long __1,
            ref BaseItem __2)
        {
            if (!isEnabled || __0 == null || __1 == 0)
            {
                return;
            }

            try
            {
                if (!IsPrimaryImageRequest(__0))
                {
                    return;
                }

                var item = __2 ?? Plugin.LibraryManager?.GetItemById(__1);
                if (item is not MusicAlbum musicAlbum || musicAlbum.GetImageInfo(ImageType.Primary, 0) != null)
                {
                    return;
                }

                if (!TryResolvePrimaryImageSource(musicAlbum, null, out var imageOwner, out var imageInfo) ||
                    imageOwner == null ||
                    imageInfo == null ||
                    imageOwner.InternalId == musicAlbum.InternalId)
                {
                    return;
                }

                __1 = imageOwner.InternalId;
                __2 = imageOwner;
            }
            catch (Exception ex)
            {
                logger?.Debug("AudioAlbumPrimaryFallback image request failed: {0}", ex.Message);
            }
        }

        private static bool IsPrimaryImageRequest(object request)
        {
            var typeProperty = request.GetType().GetProperty("Type");
            if (typeProperty?.GetValue(request) is not ImageType imageType || imageType != ImageType.Primary)
            {
                return false;
            }

            var indexProperty = request.GetType().GetProperty("Index");
            return indexProperty?.GetValue(request) is not int index || index == 0;
        }

        private static void ApplyMusicAlbumPrimaryFallback(
            object dtoService,
            MusicAlbum musicAlbum,
            User user,
            BaseItemDto dto)
        {
            if (musicAlbum.GetImageInfo(ImageType.Primary, 0) != null)
            {
                return;
            }

            if (!TryResolvePrimaryImageSource(musicAlbum, user, out var imageOwner, out var imageInfo))
            {
                return;
            }

            try
            {
                var primaryTag = getImageCacheTag?.Invoke(
                    dtoService,
                    new object[] { imageOwner, imageInfo }) as string;
                if (string.IsNullOrWhiteSpace(primaryTag))
                {
                    return;
                }

                dto.PrimaryImageItemId = imageOwner.GetClientId();
                dto.PrimaryImageTag = primaryTag;
                if (dto.ImageTags == null)
                {
                    dto.ImageTags = new Dictionary<ImageType, string>();
                }

                dto.ImageTags[ImageType.Primary] = primaryTag;
            }
            catch (Exception ex)
            {
                logger?.Debug("AudioAlbumPrimaryFallback album failed: {0}", ex.Message);
            }
        }

        private static bool TryResolvePrimaryImageSource(
            BaseItem displayParent,
            User user,
            out BaseItem imageOwner,
            out ItemImageInfo imageInfo)
        {
            imageOwner = null;
            imageInfo = null;
            if (displayParent == null)
            {
                return false;
            }

            var primaryImage = displayParent.GetImageInfo(ImageType.Primary, 0);
            if (primaryImage != null)
            {
                imageOwner = displayParent;
                imageInfo = primaryImage;
                return true;
            }

            if (displayParent is not Folder folder)
            {
                return false;
            }

            // Match Emby's folder cover behavior by borrowing the first audio child's primary image.
            var query = new InternalItemsQuery(user)
            {
                Recursive = displayParent is MusicAlbum || displayParent is Season,
                IsFolder = false,
                EnableTotalRecordCount = false,
                Limit = 1,
                ImageTypes = new[] { ImageType.Primary },
                IncludeItemTypes = new[] { nameof(Audio) }
            };

            var childWithImage = folder.GetItems(query, CancellationToken.None).Items.FirstOrDefault();
            var childPrimaryImage = childWithImage?.GetImageInfo(ImageType.Primary, 0);
            if (childWithImage == null || childPrimaryImage == null)
            {
                return false;
            }

            imageOwner = childWithImage;
            imageInfo = childPrimaryImage;
            return true;
        }
    }
}
