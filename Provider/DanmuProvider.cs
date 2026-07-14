using System;
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

namespace MediaInfoKeeper.Provider
{
    public sealed class DanmuProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Episode>,
        IHasOrder
    {
        public const string ProviderName = "Danmu";

        public string Name => ProviderName;

        public int Order => int.MaxValue - 5;

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
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult<HttpResponseInfo>(null);
        }

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Movie> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            return FetchAsync(itemResult?.Item, libraryOptions, cancellationToken);
        }

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Episode> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            return FetchAsync(itemResult?.Item, libraryOptions, cancellationToken);
        }

        private static async Task<ItemUpdateType> FetchAsync(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            if (!ShouldFetch(item, libraryOptions))
            {
                return ItemUpdateType.None;
            }

            if (Plugin.DanmuService.TryGetCachedDanmuXmlBytes(item, out _))
            {
                return ItemUpdateType.None;
            }

            try
            {
                var result = await Plugin.DanmuService
                    .TryDownloadDanmuXmlDetailedAsync(item, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Succeeded)
                {
                    return ItemUpdateType.MetadataImport;
                }

                return ItemUpdateType.None;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return ItemUpdateType.None;
            }
        }

        private static bool ShouldFetch(BaseItem item, LibraryOptions libraryOptions)
        {
            return item != null &&
                   Plugin.Instance?.Options?.MainPage?.PlugginEnabled == true &&
                   Plugin.DanmuService?.IsEnabled == true &&
                   Plugin.DanmuService.IsSupportedItem(item) &&
                   libraryOptions != null &&
                   item.IsMetadataFetcherEnabled(libraryOptions, ProviderName);
        }

    }
}
