using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.UIBaseClasses;

namespace MediaInfoKeeper.Options.View {
    internal class MainPageController : ControllerBase, IHasTabbedUIPages {
        private readonly IApplicationHost applicationHost;
        private readonly MainPageOptionsStore mainPageOptionsStore;
        private readonly PluginInfo pluginInfo;
        private readonly List<IPluginUIPageController> tabPages = new();

        public MainPageController(IApplicationHost applicationHost,
            PluginInfo pluginInfo,
            MainPageOptionsStore mainPageOptionsStore,
            MediaInfoOptionsStore mediaInfoOptionsStore,
            IntroSkipOptionsStore introSkipOptionsStore,
            NetWorkOptionsStore netWorkOptionsStore,
            EnhanceOptionsStore enhanceOptionsStore,
            MetaDataOptionsStore metaDataOptionsStore
#if DEBUG
            , DebugOptionsStore debugOptionsStore
#endif
        )
            : base(pluginInfo.Id) {
            this.applicationHost = applicationHost;
            this.pluginInfo = pluginInfo;
            this.mainPageOptionsStore = mainPageOptionsStore;

            PageInfo = new PluginPageInfo {
                Name = "MediaInfoKeeper",
                EnableInMainMenu = true,
                DisplayName = "MediaInfoKeeper",
                MenuIcon = "video_settings",
                MenuSection = "server",
                IsMainConfigPage = true
            };

            tabPages.Add(new TabPageController(pluginInfo, nameof(MediaInfoPageView), "媒体信息",
                e => new MediaInfoPageView(pluginInfo, mediaInfoOptionsStore)));

            tabPages.Add(new TabPageController(pluginInfo, nameof(MetaDataPageView), "元数据",
                e => new MetaDataPageView(pluginInfo, metaDataOptionsStore)));

            tabPages.Add(new TabPageController(pluginInfo, nameof(IntroSkipPageView), "片头片尾",
                e => new IntroSkipPageView(pluginInfo, introSkipOptionsStore)));

            tabPages.Add(new TabPageController(pluginInfo, nameof(EnhancePageView), "增强功能",
                e => new EnhancePageView(pluginInfo, enhanceOptionsStore)));

            tabPages.Add(new TabPageController(pluginInfo, nameof(NetWorkPageView), "网络代理",
                e => new NetWorkPageView(pluginInfo, netWorkOptionsStore)));

#if DEBUG
            this.tabPages.Add(new TabPageController(pluginInfo, nameof(DebugPageView), "Debug",
                e => new DebugPageView(pluginInfo, debugOptionsStore)));
#endif
        }

        public override PluginPageInfo PageInfo { get; }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => tabPages.AsReadOnly();

        public override Task<IPluginUIView> CreateDefaultPageView() {
            IPluginUIView view = new MainPageView(applicationHost, pluginInfo, mainPageOptionsStore);
            return Task.FromResult(view);
        }
    }
}
