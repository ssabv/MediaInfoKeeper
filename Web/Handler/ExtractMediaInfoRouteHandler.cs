using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class ExtractMediaInfoRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public ExtractMediaInfoRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems)
        {
            _expandToTargetItems = expandToTargetItems;
        }

        public MediaInfoMenuResponse Handle(ExtractMediaInfoRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                return response;
            }

            if (Plugin.Instance.Options.MainPage?.PlugginEnabled != true)
            {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "plugin disabled";
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids);
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                return response;
            }

            foreach (var item in targetItems)
            {
                response.Processed++;
                try
                {
                    FireAndForgetExtractSingleItem(item);
                    response.Succeeded++;
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单提取媒体信息失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "submitted";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu ExtractMediaInfo result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        private static void FireAndForgetExtractSingleItem(BaseItem item)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await MediaInfoRunner
                        .ExtractMediaInfoAsync(item.InternalId, "快捷菜单")
                        .ConfigureAwait(false);
                    if (!result)
                    {
                        Plugin.Instance.Logger.Info($"快捷菜单提取媒体信息失败或跳过: {item.Path ?? item.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Error($"快捷菜单提取媒体信息失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            });
        }
    }
}
