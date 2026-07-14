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
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Provider
{
    public sealed class DoubanMetadataProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IHasOrder
    {
        public string Name => DoubanRoleProvider.ProviderName;

        public int Order => 2;

        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateResult<Movie>(DoubanService.ResolveDoubanMetadata(info)));
        }

        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateResult<Series>(DoubanService.ResolveDoubanMetadata(info)));
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(System.Array.Empty<RemoteSearchResult>());
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(System.Array.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult<HttpResponseInfo>(null);
        }

        private static MetadataResult<TItem> CreateResult<TItem>(DoubanService.DoubanMetadataPayload payload)
            where TItem : BaseItem, new()
        {
            var result = new MetadataResult<TItem>
            {
                Item = new TItem()
            };

            if (payload == null)
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(payload.DoubanSubjectId))
            {
                result.Item.SetProviderId(External.DoubanExternalId.StaticName, payload.DoubanSubjectId);
                result.HasMetadata = true;
            }
            return result;
        }
    }
}
