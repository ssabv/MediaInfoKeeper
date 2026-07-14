namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using Emby.Web.GenericEdit.Elements;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;
    using MediaInfoKeeper.Services;

    internal class NetWorkPageView : PluginPageView
    {
        private readonly NetWorkOptionsStore store;

        public NetWorkPageView(PluginInfo pluginInfo, NetWorkOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public NetWorkOptions Options => this.ContentData as NetWorkOptions;

        public override async Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var proxyProbeTask = NetworkProbe.RunProxyLatencyAsync(this.Options);
            var tmdbProbeTask = NetworkProbe.RunTmdbAltAsync(this.Options);
            await Task.WhenAll(proxyProbeTask, tmdbProbeTask).ConfigureAwait(false);

            var proxyResult = proxyProbeTask.Result;
            this.Options.ProxyLatencyStatus = new StatusItem(proxyResult.Caption, proxyResult.StatusText, proxyResult.Status);
            this.Options.ShowProxyLatencyStatus = true;

            var tmdbResult = tmdbProbeTask.Result;
            this.Options.TmdbReplacementStatus = new StatusItem(tmdbResult.Caption, tmdbResult.StatusText, tmdbResult.Status);
            this.Options.ShowTmdbReplacementStatus = true;

            this.store.SetOptions(this.Options);
            return await base.OnSaveCommand(itemId, commandId, data).ConfigureAwait(false);
        }
    }
}
