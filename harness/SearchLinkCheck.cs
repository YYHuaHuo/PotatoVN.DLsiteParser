using GalgameManager.Enums;
using GalgameManager.Models;
using PotatoVN.App.DLsiteParser;

namespace DLsiteHarness;

/// <summary>
/// 离线验证「搜索」按钮补丁：拿同形状的假 ViewModel 走一遍，
/// 覆盖 <b>有作品号 -> 作品页</b>、<b>无作品号 -> 日文原名 -> 显示名 -> 中文名</b>，
/// 并确认关键词里不含空白（实测 DLsite 遇到 %20 会 403）。
/// </summary>
internal static class SearchLinkCheck
{
    private const string SearchPrefix = "https://www.dlsite.com/maniax/fsr/=/keyword/";
    private const string WorkPrefix = "https://www.dlsite.com/maniax/work/=/product_id/";

    internal static int Run()
    {
        Console.WriteLine("模拟设置页的「搜索」按钮（同形状假 ViewModel）");
        Console.WriteLine($"  补丁状态: {(SearchLinkPatch.Installed ? "已安装" : "未安装")}");
        if (!SearchLinkPatch.Installed)
        {
            Console.WriteLine("  结果: 失败 —— 补丁没装上（假 ViewModel 没被找到？）");
            return 1;
        }

        var ok = true;

        // ① 有作品号 -> 作品页
        var withId = NewViewModel("サンプルの表示名", "サンプル 原名 副題");
        withId.Gal.IdForPlugins[Plugin.ParserId] = "RJ00000001";
        SwitchToUs(withId);
        var idUrl = withId.SearchUri;
        var idOk = idUrl == WorkPrefix + "RJ00000001.html";
        ok &= idOk;
        Console.WriteLine($"  ① 有作品号 -> 作品页      : {idOk}");
        Console.WriteLine($"      {idUrl}");

        // ② 无作品号、有日文原名 -> 用原名（截短、去空白）
        var withOriginal = NewViewModel("中文显示名", "サンプル 原名 副題");
        SwitchToUs(withOriginal);
        var originalOk = withOriginal.SearchUri == SearchPrefix + Uri.EscapeDataString("サンプル");
        ok &= originalOk;
        Console.WriteLine($"  ② 无 id、有原名 -> 用原名  : {originalOk}");
        Console.WriteLine($"      {withOriginal.SearchUri}");

        // ③ 只有中文名 -> 用中文名（找不到日文原名时的兜底）
        var chineseOnly = NewViewModel("千恋万花", null);
        SwitchToUs(chineseOnly);
        var chineseOk = chineseOnly.SearchUri == SearchPrefix + Uri.EscapeDataString("千恋万花");
        ok &= chineseOk;
        Console.WriteLine($"  ③ 只有中文名 -> 用中文名   : {chineseOk}");
        Console.WriteLine($"      {chineseOnly.SearchUri}");

        // ④ 关键词里绝不能出现 %20（DLsite 见到就 403）
        var anySpace = withOriginal.SearchUri.Contains("%20", StringComparison.Ordinal) ||
                       chineseOnly.SearchUri.Contains("%20", StringComparison.Ordinal) ||
                       idUrl.Contains("%20", StringComparison.Ordinal);
        ok &= !anySpace;
        Console.WriteLine($"  ④ 关键词不含 %20（不触发 403）: {!anySpace}");

        // ⑤ 切到内置源时不该动 PotatoVN 自己的 URL
        var untouched = withId.SearchUri;
        withId.SelectedRss = RssType.Bangumi;
        var keepOk = withId.SearchUri == untouched;
        ok &= keepOk;
        Console.WriteLine($"  ⑤ 切到内置源时不动它      : {keepOk}");

        Console.WriteLine($"  结果: {(ok ? "全部通过" : "有失败项")}");
        return ok ? 0 : 1;
    }

    private static GalgameManager.ViewModels.GalgameSettingViewModel NewViewModel(string displayName, string? originalName) =>
        new()
        {
            Gal = new Galgame
            {
                Name = displayName,
                OriginalName = originalName ?? string.Empty,
            },
        };

    /// <summary>先切到别的源再切回本插件 —— 与 PotatoVN 里"换源"的动作一致。</summary>
    private static void SwitchToUs(GalgameManager.ViewModels.GalgameSettingViewModel vm)
    {
        vm.SelectedRss = RssType.Bangumi;
        vm.SelectedRss = (RssType)Plugin.ParserId;
    }
}