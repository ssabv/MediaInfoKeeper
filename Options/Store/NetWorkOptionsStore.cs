namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class NetWorkOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public NetWorkOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public NetWorkOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            var networkOptions = options.GetNetWorkOptions();
            networkOptions.ProxyLatencyStatus = new Emby.Web.GenericEdit.Elements.StatusItem();
            networkOptions.ShowProxyLatencyStatus = false;
            networkOptions.TmdbReplacementStatus = new Emby.Web.GenericEdit.Elements.StatusItem();
            networkOptions.ShowTmdbReplacementStatus = false;
            return networkOptions;
        }

        public void SetOptions(NetWorkOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.NetWork = options ?? new NetWorkOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
