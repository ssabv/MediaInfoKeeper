namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;

    internal class EnhancePageView : PluginPageView
    {
        private readonly EnhanceOptionsStore store;

        public EnhancePageView(PluginInfo pluginInfo, EnhanceOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public EnhanceOptions Options => this.ContentData as EnhanceOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            this.ContentData = this.store.GetOptions();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
