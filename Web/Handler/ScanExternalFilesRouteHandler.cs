using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class ScanExternalFilesRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public ScanExternalFilesRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems)
        {
            _expandToTargetItems = expandToTargetItems;
        }

        public MediaInfoMenuResponse Handle(ScanExternalFilesRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "未选择条目";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu ScanExternalFiles result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            if (Plugin.ExternalFiles == null || !Plugin.ExternalFiles.IsAvailable)
            {
                response.Message = "外挂文件扫描不可用";
                response.Failed = request.Ids.Length;
                Plugin.Instance.Logger.Error("ShortcutMenu ScanExternalFiles failed: ExternalFiles is unavailable.");
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids).OfType<Video>().ToList();
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "没有可扫描的视频条目";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu ScanExternalFiles result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var refreshOptions = Plugin.ExternalFiles.GetRefreshOptions();

            foreach (var item in targetItems)
            {
                response.Processed++;

                try
                {
                    if (!Plugin.ExternalFiles.HasExternalFilesChanged(item, refreshOptions.DirectoryService, true))
                    {
                        response.Skipped++;
                        continue;
                    }

                    Plugin.ExternalFiles
                        .UpdateExternalFiles(item, refreshOptions, false, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    response.Succeeded++;
                    Plugin.Instance.Logger.Info($"ShortcutMenu 扫描外挂文件已更新: {item.Path ?? item.Name}");
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单扫描外挂文件失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = response.Succeeded > 0 ? "扫描外挂文件完成" : "未检测到外挂文件变化";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu ScanExternalFiles result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}
