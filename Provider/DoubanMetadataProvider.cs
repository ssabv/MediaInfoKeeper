using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaInfoKeeper.External;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Provider {
    public sealed class DoubanMetadataProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        IHasOrder {
        public int Order => 2;
        public string Name => DoubanRoleProvider.ProviderName;

        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken) {
            return Task.FromResult(CreateResult<Movie>(DoubanService.ResolveDoubanMetadata(info)));
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo,
            CancellationToken cancellationToken) {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken) {
            return Task.FromResult<HttpResponseInfo>(null);
        }

        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken) {
            return Task.FromResult(CreateResult<Series>(DoubanService.ResolveDoubanMetadata(info)));
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo,
            CancellationToken cancellationToken) {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken) {
            return Task.FromResult(new MetadataResult<Episode> {
                Item = new Episode()
            });
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo,
            CancellationToken cancellationToken) {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        private static MetadataResult<TItem> CreateResult<TItem>(DoubanService.DoubanMetadataPayload payload)
            where TItem : BaseItem, new() {
            var result = new MetadataResult<TItem> {
                Item = new TItem()
            };

            if (payload == null) return result;

            if (!string.IsNullOrWhiteSpace(payload.DoubanSubjectId)) {
                result.Item.SetProviderId(DoubanExternalId.StaticName, payload.DoubanSubjectId);
                result.HasMetadata = true;
            }

            return result;
        }
    }
}
