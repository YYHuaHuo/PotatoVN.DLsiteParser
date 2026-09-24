using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Contracts.Phrase;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Models;

namespace PotatoVN.App.DLsiteParser;

/// <summary>
/// 插件主类。
/// <para>
/// 这个插件只做一件事：提供一个 DLsite 搜刮器，因此实现 <see cref="IParserProvider"/>。
/// </para>
/// <para>
/// 注意：<c>csproj</c> 里的 <c>AssemblyName</c> 必须是 <c>A{Info.Id}</c>，两者要一致。
/// </para>
/// </summary>
public class Plugin : IPlugin, IParserProvider
{
    /// <summary>
    /// 本插件搜刮器的 ParserId。
    /// <para>
    /// 约束（来自 <c>IParserProvider</c> / <c>RssType</c> 的约定）：
    /// 必须 <b>&gt; 100</b>，且要在所有插件中唯一（避开别人的随机值）。
    /// 宿主用 <c>(RssType)ParserId</c> 强转，并把 id 存进 <c>Galgame.IdForPlugins</c>。
    /// </para>
    /// </summary>
    public const int ParserId = 769164;

    /// <summary>
    /// 插件 GUID。必须保持不变，并且与 csproj 的 AssemblyName（<c>A{GUID}</c>）一致。
    /// </summary>
    private static readonly Guid PluginId = new("8738f51b-0217-49d1-a3a4-50e9411feb48");

    /// <summary>
    /// 宿主 API。在 <see cref="InitializeAsync"/> 中被赋值。
    /// </summary>
    public static IPotatoVnApi HostApi { get; private set; } = null!;

    /// <summary>
    /// 搜刮器实例。<see cref="IParserProvider.GetPhraser"/> 只会在插件加载时被调用一次，
    /// 所以这里只创建一次并长期持有。
    /// </summary>
    private readonly DLsitePhraser _phraser = new();

    public PluginInfo Info { get; } = new()
    {
        Id = PluginId,
        Name = "DLsite 搜刮器",
        Description = "从 DLsite 获取作品信息：标题、社团、发售日、标签、封面与简介。\n" +
                      "支持 maniax / pro / girls / soft 各分区，作品 id 形如 RJ123456、VJ123456、BJ123456。",
    };

    public Task InitializeAsync(IPotatoVnApi hostApi)
    {
        HostApi = hostApi;

        // 说明：IPotatoVnApi 并没有暴露 HttpClient 给插件（只有 DownloadImageAsync 可传 null 用宿主的默认客户端）。
        // DLsite 抓取需要自定义 UA 与年龄确认 Cookie，所以搜刮器自己持有一个 HttpClient。
        // 插件也不应该自己往宿主 UI 上乱弹窗，出错走 hostApi.Log / DeveloperEvent。

        return Task.CompletedTask;
    }

    /// <summary>
    /// 插件被卸载 / 停用时调用。本插件没有需要清理的外部资源。
    /// </summary>
    public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
    {
        if (cts.IsCancellationRequested) return Task.FromCanceled(cts);
        _phraser.Dispose();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // IParserProvider
    // ---------------------------------------------------------------------

    /// <inheritdoc />
    public IGalInfoPhraser GetPhraser() => _phraser;

    /// <inheritdoc />
    public string ParserName => "DLsite";
}
