using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Provider
{
    public sealed class DoubanRoleProvider :
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Series>,
        IForcedProvider,
        IHasOrder
    {
        public const string ProviderName = "DoubanRole";

        public string Name => ProviderName;

        public int Order => int.MaxValue;

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Movie> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Enhance(itemResult.Item, itemResult.People));
        }

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Series> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Enhance(itemResult.Item, itemResult.People));
        }

        private static ItemUpdateType Enhance(MediaBrowser.Controller.Entities.BaseItem item, List<PersonInfo> people)
        {
            if (item == null || people == null || people.Count == 0)
            {
                return ItemUpdateType.None;
            }

            var libraryOptions = Plugin.LibraryManager?.GetLibraryOptions(item);
            if (libraryOptions == null || !item.IsMetadataFetcherEnabled(libraryOptions, ProviderName))
            {
                return ItemUpdateType.None;
            }

            return DoubanService.EnhancePeopleRole(item, people)
                ? ItemUpdateType.MetadataImport
                : ItemUpdateType.None;
        }
    }
}
