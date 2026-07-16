using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.External;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Provider {
    public sealed class DoubanRoleProvider :
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Series>,
        ICustomMetadataProvider<Episode>,
        IForcedProvider,
        IHasOrder {
        public const string ProviderName = "DoubanRole";

        private static readonly DoubanMetadataProvider MetadataProvider = new();

        public string Name => ProviderName;

        public async Task<ItemUpdateType> FetchAsync(
            MetadataResult<Movie> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken) {
            var item = itemResult.Item;
            if (!ShouldEnhance(item, itemResult.People, libraryOptions)) return ItemUpdateType.None;

            var changed = false;
            if (string.IsNullOrWhiteSpace(item.GetProviderId(DoubanExternalId.StaticName))) {
                var metadataResult = await MetadataProvider.GetMetadata(item.GetLookupInfo(libraryOptions), cancellationToken);
                var subjectId = metadataResult.Item.GetProviderId(DoubanExternalId.StaticName);
                if (!string.IsNullOrWhiteSpace(subjectId)) {
                    item.SetProviderId(DoubanExternalId.StaticName, subjectId);
                    changed = true;
                }
            }

            return Enhance(item, itemResult.People, changed);
        }

        public async Task<ItemUpdateType> FetchAsync(
            MetadataResult<Series> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken) {
            var item = itemResult.Item;
            if (!ShouldEnhance(item, itemResult.People, libraryOptions)) return ItemUpdateType.None;

            var changed = false;
            if (string.IsNullOrWhiteSpace(item.GetProviderId(DoubanExternalId.StaticName))) {
                var metadataResult = await MetadataProvider.GetMetadata(item.GetLookupInfo(libraryOptions), cancellationToken);
                var subjectId = metadataResult.Item.GetProviderId(DoubanExternalId.StaticName);
                if (!string.IsNullOrWhiteSpace(subjectId)) {
                    item.SetProviderId(DoubanExternalId.StaticName, subjectId);
                    changed = true;
                }
            }

            return Enhance(item, itemResult.People, changed);
        }

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Episode> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken) {
            var item = itemResult.Item;
            if (!ShouldEnhance(item, itemResult.People, libraryOptions))
                return Task.FromResult(ItemUpdateType.None);

            return Task.FromResult(Enhance(item, itemResult.People, false));
        }

        // 必须在其他自定义元数据 Provider 之后执行，避免豆瓣角色名被后续刮削器覆盖。
        public int Order => int.MaxValue;

        private static bool ShouldEnhance(BaseItem item, List<PersonInfo> people, LibraryOptions libraryOptions) {
            if (item == null || people == null || people.Count == 0) return false;

            return libraryOptions != null && item.IsMetadataFetcherEnabled(libraryOptions, ProviderName);
        }

        private static ItemUpdateType Enhance(BaseItem item, List<PersonInfo> people, bool changed) {
            if (DoubanService.EnhancePeopleRole(item, people)) changed = true;

            return changed ? ItemUpdateType.MetadataImport : ItemUpdateType.None;
        }
    }
}
