namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class IntroSkipOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public IntroSkipOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public IntroSkipOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            var introSkipOptions = options.IntroSkip ?? new IntroSkipOptions();
            introSkipOptions.Initialize();
            return introSkipOptions;
        }

        public void SetOptions(IntroSkipOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.IntroSkip = options ?? new IntroSkipOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
