using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GalgameManager.Enums;
using GalgameManager.WinApp.Base.Contracts;
using HarmonyLib;

namespace PotatoVN.App.DLsiteParser;

/// <summary>
/// 让设置页那个「搜索」按钮在选中本插件时跳到 DLsite。
/// <para>
/// 背景：PotatoVN 把每个信息源的搜索页 URL 写死在一张 <c>Dictionary&lt;int, string&gt;</c> 里
/// （只有 Bangumi / Vndb / Mixed / Ymgal / Cngal），插件源查不到，于是点「搜索」会停在
/// 别的源的搜索页。PotatoVN 也没有给插件留"搜索引擎"接口，所以这里用 Harmony 给那个
/// ViewModel 打一个后置补丁，替它补上本插件源的 URL。
/// </para>
/// <para>
/// 跳哪去（优先级）：
/// <list type="number">
/// <item>有 DLsite 作品号（<c>RJ/VJ/BJ…</c>）-> 直接开<b>作品页</b>，最准；</item>
/// <item>否则按名字搜：<b>日文原名 -> 显示名 -> 中文名</b>，取第一个非空的。</item>
/// </list>
/// 关键词必须**不含空白**：实测 DLsite 的 <c>fsr</c> 搜索遇到 <c>%20</c> 直接 403
/// （词长不影响），所以按搜刮时同款规则截到第一个分隔符，再去掉所有空白。
/// </para>
/// <para>
/// 安全约定：<b>只在自己是当前选中源时</b>改 URL；PotatoVN 结构对不上就什么都不做，
/// 只写一条日志。补丁体自身绝不向外抛异常。
/// </para>
/// </summary>
internal static class SearchLinkPatch
{
    private const string HarmonyId = "dsh.potatovn.dlsite-parser.search-link";

    /// <summary>PotatoVN 里管这个页面的 ViewModel（用字符串找，避免编译期依赖宿主程序集）。</summary>
    private const string ViewModelTypeName = "GalgameManager.ViewModels.GalgameSettingViewModel";

    /// <summary>搜刮时首选分区也是 maniax；作品页用 maniax 域时 DLsite 自己会解析到正确分区（实测 VJ 号也 200）。</summary>
    private const string WorkUrlFormat = "https://www.dlsite.com/maniax/work/=/product_id/{0}.html";

    /// <summary>按名字搜索的页面（关键词必须无空白，见类型注释）。</summary>
    private const string SearchUrlPrefix = "https://www.dlsite.com/maniax/fsr/=/keyword/";

    /// <summary>按名字搜索时依次尝试的属性。</summary>
    private static readonly string[] NameProperties = { "OriginalName", "Name", "CnName", "ChineseName" };

    /// <summary>截短用的分隔符（与搜刮时派生搜索词变体用的是同一套）。</summary>
    private static readonly char[] KeywordDelimiters = { ' ', '\u3000', '～', '~', '（', '(', '【', '「', '［', '[' };

    /// <summary>本补丁验证过的 PotatoVN 版本。</summary>
    private const string VerifiedHostVersion = "1.10.2.0";

    private static MethodInfo? _getSelectedRss;
    private static MethodInfo? _getSearchUri;
    private static MethodInfo? _setSearchUri;
    private static MethodInfo? _getGal;

    internal static bool Installed { get; private set; }

    internal static void Install(IPotatoVnApi host)
    {
        try
        {
            var type = FindType(ViewModelTypeName);
            if (type is null)
            {
                host.Log(msg: $"在 PotatoVN 里找不到 {ViewModelTypeName}，「搜索」按钮保持原样。");
                return;
            }

            var target = type.GetMethod("OnSelectedRssChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _getSelectedRss = type.GetProperty("SelectedRss")?.GetGetMethod();
            _getSearchUri = type.GetProperty("SearchUri")?.GetGetMethod();
            _setSearchUri = type.GetProperty("SearchUri")?.SetMethod;
            _getGal = type.GetProperty("Gal")?.GetGetMethod();

            if (target is null || _getSelectedRss is null || _setSearchUri is null || _getGal is null)
            {
                host.Log(msg: "PotatoVN 设置页的结构与预期不符（OnSelectedRssChanged / SelectedRss / SearchUri / Gal 至少缺一个），" +
                              "「搜索」按钮保持原样。");
                return;
            }

            new Harmony(HarmonyId).Patch(target,
                postfix: new HarmonyMethod(typeof(SearchLinkPatch), nameof(SelectedRssChangedPostfix)));

            Installed = true;
            host.Log(msg: $"已接管设置页的「搜索」按钮：选中本插件时跳到 DLsite（有作品号走作品页，否则按 原名->显示名->中文名 搜索；" +
                          $"验证于 {VerifiedHostVersion}）。");
        }
        catch (Exception e)
        {
            host.Log(msg: $"「搜索」按钮的补丁没打上（{e.GetType().Name}: {e.Message}），按钮行为保持原样。");
        }
    }

    /// <summary>
    /// 后置补丁体：PotatoVN 刚把选中源改成 <paramref name="__instance"/> 上的值。
    /// 是我们的源就顺手把 URL 也设上（设属性会触发通知，界面上的链接才会跟着变）。
    /// </summary>
    private static void SelectedRssChangedPostfix(object __instance)
    {
        try
        {
            if (!Installed || __instance is null) return;
            if (_getSelectedRss?.Invoke(__instance, null) is not RssType rss) return;
            if ((int)rss != Plugin.ParserId) return;

            var gal = _getGal?.Invoke(__instance, null);
            if (gal is null) return;

            var url = BuildUrl(gal);
            if (url is null) return;
            if (_getSearchUri?.Invoke(__instance, null) as string == url) return;

            _setSearchUri?.Invoke(__instance, new object?[] { url });
        }
        catch
        {
            // 补丁体绝不把异常抛回宿主
        }
    }

    /// <summary>有作品号就进作品页，否则按名字搜索；都没有就返回 null（保持 PotatoVN 原样）。</summary>
    private static string? BuildUrl(object gal)
    {
        var id = PluginWorkId(gal);
        if (!string.IsNullOrWhiteSpace(id)) return string.Format(WorkUrlFormat, id);

        foreach (var property in NameProperties)
        {
            var candidate = TrimKeyword(ReadString(gal, property));
            if (candidate.Length == 0) continue;
            return SearchUrlPrefix + Uri.EscapeDataString(candidate);
        }

        return null;
    }

    /// <summary>
    /// 取 DLsite 作品号：PotatoVN 把插件源的作品号存在 <c>Galgame.IdForPlugins[ParserId]</c>；
    /// 另外装了「新增源插件」时，<c>Galgame.Id</c> 也会路由到这个值。
    /// </summary>
    private static string? PluginWorkId(object gal)
    {
        if (gal.GetType().GetProperty("IdForPlugins")?.GetValue(gal) is IDictionary<int, string> map &&
            map.TryGetValue(Plugin.ParserId, out var fromMap) && !string.IsNullOrWhiteSpace(fromMap))
        {
            return fromMap.Trim();
        }

        var raw = ReadString(gal, "Id");
        return IsWorkId(raw) ? raw!.Trim() : null;
    }

    private static bool IsWorkId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value!.Length >= 8 &&
        (value.StartsWith("RJ", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("VJ", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("BJ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 搜索关键词：截到第一个分隔符，再去掉所有空白。
    /// （实测 DLsite 的 fsr 搜索遇到 <c>%20</c> 会 403，而词长本身没问题。）
    /// </summary>
    private static string TrimKeyword(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var text = name.Trim();
        foreach (var delimiter in KeywordDelimiters)
        {
            var index = text.IndexOf(delimiter);
            if (index > 0) text = text[..index];
        }

        return new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>读字符串属性：可能是 <c>string</c>，也可能是带 <c>Value</c> 的包装类型。</summary>
    private static string? ReadString(object target, string propertyName)
    {
        var holder = target.GetType().GetProperty(propertyName)?.GetValue(target);
        if (holder is null) return null;
        if (holder is string direct) return direct;
        return holder.GetType().GetProperty("Value")?.GetValue(holder) as string;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null) return type;
        }
        return null;
    }
}