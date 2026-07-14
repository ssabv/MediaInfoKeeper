using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Provider
{
    public sealed class TheIntroDbProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Episode>,
        IHasOrder
    {
        public const string ProviderName = "TheIntroDB";
        private const string MarkerSuffix = "#MIKTIDB";

        public string Name => ProviderName;

        public int Order => int.MaxValue - 10;

        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Movie>
            {
                Item = new Movie()
            });
        }

        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Episode>
            {
                Item = new Episode()
            });
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(System.Array.Empty<RemoteSearchResult>());
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(System.Array.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult<HttpResponseInfo>(null);
        }

        public async Task<ItemUpdateType> FetchAsync(
            MetadataResult<Movie> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            var item = itemResult?.Item;
            if (!ShouldFetch(item, libraryOptions))
            {
                return ItemUpdateType.None;
            }

            var result = await TheIntroDbService.GetMarkersAsync(item, cancellationToken).ConfigureAwait(false);
            return ApplyMarkers(item, result);
        }

        public async Task<ItemUpdateType> FetchAsync(
            MetadataResult<Episode> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            var item = itemResult?.Item;
            if (!ShouldFetch(item, libraryOptions))
            {
                return ItemUpdateType.None;
            }

            var result = await TheIntroDbService.GetMarkersAsync(item, cancellationToken).ConfigureAwait(false);
            return ApplyMarkers(item, result);
        }

        private static bool ShouldFetch(BaseItem item, LibraryOptions libraryOptions)
        {
            return item != null &&
                   libraryOptions != null &&
                   item.IsMetadataFetcherEnabled(libraryOptions, ProviderName);
        }

        private static ItemUpdateType ApplyMarkers(BaseItem item, TheIntroDbService.MarkerLookupResult result)
        {
            if (item == null || result?.Found != true)
            {
                return ItemUpdateType.None;
            }

            var hasIntro = result.IntroStartTicks.HasValue &&
                           result.IntroEndTicks.HasValue &&
                           result.IntroEndTicks.Value > result.IntroStartTicks.Value;
            var hasCredits = result.CreditsStartTicks.HasValue &&
                             (!item.RunTimeTicks.HasValue || result.CreditsStartTicks.Value < item.RunTimeTicks.Value);
            if (!hasIntro && !hasCredits)
            {
                return ItemUpdateType.None;
            }

            var chapters = Plugin.IntroSkipChapterApi.GetChapters(item) ?? new List<ChapterInfo>();
            chapters.RemoveAll(chapter =>
                chapter?.MarkerType == MarkerType.IntroStart ||
                chapter?.MarkerType == MarkerType.IntroEnd ||
                chapter?.MarkerType == MarkerType.CreditsStart);

            if (hasIntro)
            {
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = result.IntroStartTicks.Value
                });
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = result.IntroEndTicks.Value
                });
            }

            if (hasCredits)
            {
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.CreditsStart + MarkerSuffix,
                    MarkerType = MarkerType.CreditsStart,
                    StartPositionTicks = result.CreditsStartTicks.Value
                });
            }

            IntroMarkerProtect.SaveChapters(
                Plugin.Instance.ItemRepository,
                item,
                chapters,
                new[] { MarkerType.IntroStart, MarkerType.IntroEnd, MarkerType.CreditsStart },
                filterPlainChapters: false);
            Plugin.Instance.Logger.Info("TheIntroDB 标记写入成功: {0}", TheIntroDbService.FormatItemForLog(item));
            return ItemUpdateType.MetadataImport;
        }
    }
}
