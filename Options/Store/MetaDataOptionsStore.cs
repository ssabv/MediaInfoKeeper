namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class MetaDataOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public MetaDataOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public MetaDataOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.MetaData ?? new MetaDataOptions();
        }

        public void SetOptions(MetaDataOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.MetaData = options ?? new MetaDataOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
