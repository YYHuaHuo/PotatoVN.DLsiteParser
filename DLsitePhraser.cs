using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using GalgameManager.Contracts.Phrase;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;

namespace PotatoVN.App.DLsiteParser;

/// <summary>
/// DLsite 搜刮器。
///
/// <para>
/// <b>重要：本文件的站点解析部分是「未验证」的。</b>
/// 编写时开发环境无法访问 dlsite.com（DNS 解析到非公网地址），
/// 因此所有 URL / 标签文字 / 结构假设都没有经过真实页面校验。
/// 代码据此做了两方面防护：
/// 1. 尽量不依赖具体 CSS 类名，而是走 <c>og:</c> 元标签 + 「按表头文字扫描表格」的方式，
///    这样站点改 class 名不会立刻失效；
/// 2. 解析结果为空时返回 null（见 <see cref="DLsiteWork.HasAnyUsefulData"/>），
///    宁可搜刮失败也不会把空数据写进游戏库。
/// </para>
///
/// <para>
/// 若首次运行搜不到数据，把 <see cref="DumpRawResponseForDiagnosis"/> 打开，
/// 会在插件目录下写出原始响应，据此可以精确定位需要改的地方。
/// </para>
/// </summary>
public sealed class DLsitePhraser :
    IGalInfoPhraser,
    IGalCoversParser,
    IGalHeadersParser,
    IGalStaffParser,
    IHttpClientProvider,
    IDisposable
{
    // =====================================================================
    // 集中在这里的站点相关假设 —— 出问题时优先改这一段
    // =====================================================================

    /// <summary>
    /// 依次尝试的分区。DLsite 的作品 URL 必须带对分区，否则 404。
    /// maniax=同人(男性向), pro=商业, soft=全年齢软件, girls=女性向, comic=漫画, home=PC软件。
    /// </summary>
    private static readonly string[] Sections = ["maniax", "pro", "soft", "girls", "comic", "home"];

    /// <summary>作品页 URL（未验证）。</summary>
    private static string WorkUrl(string section, string productId) =>
        $"https://www.dlsite.com/{section}/work/=/product_id/{productId}.html";

    /// <summary>
    /// 前端 JSON 接口（未验证）。能通就优先用它——比 HTML 稳得多。
    /// 不通会静默回退到 HTML 解析。
    /// </summary>
    private static string JsonApiUrl(string section, string productId) =>
        $"https://www.dlsite.com/{section}/api/=/product.json?workno={productId}";

    /// <summary>
    /// 按作品名搜索（实测可用）。分区必须带上：DLsite 的搜索是**按站点分区**的，
    /// 拿 maniax 去搜 pro 的作品会搜不到。
    /// </summary>
    private static string SearchUrl(string section, string keyword) =>
        $"https://www.dlsite.com/{section}/fsr/=/keyword/{Uri.EscapeDataString(keyword)}";

    /// <summary>名称搜索可接受的最低相似度。低于它宁可放弃，也不把错误的作品写进库。</summary>
    private const double MinSearchScore = 0.75;

    /// <summary>DLsite 是 UTF-8。若出现乱码，检查这里。</summary>
    private static readonly Encoding ResponseEncoding = Encoding.UTF8;

    /// <summary>
    /// 表格里「表头文字」到字段的映射。
    /// 采用「扫描 th 的文字」而不是「写死 class 名」，抗改版能力强很多。
    /// </summary>
    private const string LabelCircle = "サークル名";
    private const string LabelBrand = "ブランド名";
    private const string LabelReleaseDate = "発売日";

    /// <summary>
    /// 实测：现在的作品页用的是「<b>販売日</b>」，早已不是「発売日」（后者一次都没出现）。
    /// 两个都认，站点再改回去也不会瞎。
    /// </summary>
    private const string LabelReleaseDateAlt = "販売日";
    private const string LabelGenre = "ジャンル";
    private const string LabelSeries = "シリーズ名";
    private const string LabelWorkType = "作品形式";
    private const string LabelAgeRating = "年齢指定";
    private const string LabelPrice = "価格";
    private const string LabelVoiceActor = "声優";
    private const string LabelIllustrator = "イラスト";
    private const string LabelScenario = "シナリオ";
    private const string LabelMusic = "音楽";

    /// <summary>设为 true 时把原始响应写到插件目录，用于排查解析失败。</summary>
    private static bool DumpRawResponseForDiagnosis =>
        Environment.GetEnvironmentVariable("DLsiteParser_DumpRaw") == "1";

    // =====================================================================
    // 基础设施
    // =====================================================================

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private readonly HttpClient _client;

    public DLsitePhraser()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        // DLsite 的 R18 作品会先过一个年龄确认页。
        // 业界通行做法是直接带上年龄确认 Cookie（未验证，可能需要额外参数）。
        handler.CookieContainer.Add(new Cookie("age_check_done", "1", "/", ".dlsite.com"));

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
    }

    /// <inheritdoc />
    public HttpClient? HttpClient => _client;

    public void Dispose() => _client.Dispose();

    // =====================================================================
    // IGalInfoPhraser
    // =====================================================================

    public RssType GetPhraseType() => (RssType)Plugin.ParserId;

    public async Task<Galgame?> GetGalgameInfo(Galgame galgame)
    {
        try
        {
            var productId = GetProductId(galgame);

            // 没有 id 就退化为按名字搜索（未验证，失败就返回 null）
            if (string.IsNullOrWhiteSpace(productId))
            {
                productId = await TryFindProductIdByNameAsync(galgame);
            }

            if (string.IsNullOrWhiteSpace(productId)) return null;

            var work = await FetchWorkAsync(productId);
            if (work is null || !work.HasAnyUsefulData)
            {
                Log($"DLsite 解析 {productId} 未得到有效信息，可能是站点改版或作品不存在。");
                return null;
            }

            return ToGalgame(work, galgame);
        }
        catch (Exception e)
        {
            // 宿主会兜住插件异常，但这里自己吞掉更干净：搜刮失败不应该影响其它流程。
            Log($"DLsite 搜刮失败：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// DLsite id 存在 <c>Galgame.IdForPlugins</c> 里
    /// （<c>RssType &gt;= 100</c> 的 id 走这个字典，而不是内置的 9 元素 <c>Ids</c> 数组）。
    /// </summary>
    private static string? GetProductId(Galgame galgame)
    {
        if (!galgame.IdForPlugins.TryGetValue(Plugin.ParserId, out var raw)) return null;
        return NormalizeProductId(raw);
    }

    private static readonly Regex ProductIdRegex =
        new(@"^[A-Z]{2}\d{6,8}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>把用户可能填的各种写法归一化成 <c>RJ123456</c> 这种形式。</summary>
    private static string? NormalizeProductId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim().ToUpperInvariant();

        // 允许用户直接粘贴作品页 URL
        var m = Regex.Match(s, @"PRODUCT_ID/([A-Z]{2}\d{6,8})");
        if (m.Success) s = m.Groups[1].Value;

        return ProductIdRegex.IsMatch(s) ? s : null;
    }

    /// <summary>
    /// 同一个作品的短期缓存。
    /// <para>
    /// 宿主在一次搜刮流程里会分别调用 <see cref="GetGalgameInfo"/>、
    /// <see cref="GetGalCoversAsync"/>、<see cref="GetGalHeadersAsync"/>、
    /// <see cref="GetStaffsAsync"/>，如果不缓存就会对同一个页面请求 4 次。
    /// 缓存能显著减少对 DLsite 的压力（也降低被封的风险）。
    /// </para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DLsiteWork Work, DateTime At)>
        _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 依次尝试各分区，返回第一个能拿到的作品数据。
    /// </summary>
    private async Task<DLsiteWork?> FetchWorkAsync(string productId)
    {
        if (_cache.TryGetValue(productId, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
            return hit.Work;

        DLsiteWork? partial = null;

        foreach (var section in Sections)
        {
            // 1) 先试 JSON 接口。实测可用，而且它返回的 site_id 就是作品的真实分区
            //    （maniax / pro / …），比逐个分区去猜 HTML 准得多。
            var fromJson = await TryFetchFromJsonApiAsync(section, productId);

            // 2) HTML 页**也要抓**，不能因为 JSON 有数据就跳过。
            //    实测 JSON 的短板：intro 恒为 null（正文在 intro_s）；不少作品的 creaters
            //    是空数组，人只在 HTML 的「声優 / 音楽」行里；og:image / og:description 反而更稳。
            //    两个来源靠 Merge 互补。JSON 已给出分区时只抓那一个，避免 6 个分区各抓一遍。
            var htmlSection = fromJson?.Section is { Length: > 0 } s ? s : section;
            var html = await GetStringAsync(WorkUrl(htmlSection, productId));
            var fromHtml = string.IsNullOrWhiteSpace(html)
                ? null
                : await ParseHtmlAsync(html!, productId, htmlSection);

            var merged = Merge(fromJson, fromHtml);
            if (merged is not null)
            {
                // JSON 那条路不经过 ParseHtmlAsync，所以绝对化要在这里统一补一次
                //（实测 image_main.url 是协议相对的 //img.dlsite.jp/…，不补的话宿主取不到图）。
                NormalizeUrls(merged);
                merged.Title = CleanTitle(merged.Title);
            }

            if (merged is { HasAnyUsefulData: true })
            {
                _cache[productId] = (merged, DateTime.UtcNow);
                return merged;
            }

            // 本分区没拿到完整数据，但可能捞到了一点，先留着继续试下一个分区
            partial ??= merged;
        }

        // 所有分区都没拿出完整数据：能用就用部分结果，否则放弃
        if (partial is { HasAnyUsefulData: true })
        {
            _cache[productId] = (partial, DateTime.UtcNow);
            return partial;
        }

        return null;
    }

    private async Task<string?> GetStringAsync(string url)
    {
        try
        {
            using var resp = await _client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var text = ResponseEncoding.GetString(bytes);
            DumpIfRequested(url, text);
            return text;
        }
        catch (Exception e)
        {
            Log($"请求 {url} 失败：{e.Message}");
            return null;
        }
    }

    private static void DumpIfRequested(string url, string text)
    {
        if (!DumpRawResponseForDiagnosis) return;
        try
        {
            var dir = Plugin.HostApi?.GetPluginPath() ?? AppContext.BaseDirectory;
            var name = "dlsite-dump-" + Regex.Replace(url, @"[^A-Za-z0-9]+", "_") + ".html";
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name), text);
        }
        catch
        {
            // 诊断用，失败无所谓
        }
    }

    // =====================================================================
    // JSON 接口（未验证）
    // =====================================================================

    private async Task<DLsiteWork?> TryFetchFromJsonApiAsync(string section, string productId)
    {
        var json = await GetStringAsync(JsonApiUrl(section, productId));
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 有的实现返回数组，有的返回对象，都兼容一下
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return null;

            var work = new DLsiteWork
            {
                ProductId = productId,
                Section = section,
                // 字段名都是「多候选」读取：不同接口版本用词不完全一致
                Title = ReadString(root, "product_name", "work_name", "title"),
                Circle = ReadString(root, "circle_name", "maker_name", "brand_name", "circle"),
                // 实测：intro 恒为 null（正文在 intro_s），两个都试。
                Description = ReadString(root, "intro", "intro_s", "description", "work_intro", "body"),
                // 实测：没有 image_url / thumb / main_image_url 这些键。
                // 封面在 image_main.url（对象里），另有一个现成的字符串 image_thumb。
                CoverUrl = ReadImageUrl(root, "image_main", "image_thum")
                           ?? ReadString(root, "image_thumb", "image_url", "thumb", "thumbnail"),
                // 未找到「横版头图」对应的键（wide_image_url 之类都不存在），先留空，
                // 不拿封面冒充头图。
                HeaderUrl = ReadString(root, "wide_image_url", "header_image_url"),
                SeriesName = ReadString(root, "series_name", "series"),
                WorkType = ReadString(root, "work_type", "work_type_string", "category"),
                AgeRating = ReadString(root, "age_category_string", "age_rating", "age_category"),
            };

            // site_id 就是分区名（实测 maniax / pro / …）。有了它，抓 HTML 时不必逐分区试。
            var siteId = ReadString(root, "site_id");
            if (siteId is not null && Sections.Contains(siteId)) work.Section = siteId;

            var dateStr = ReadString(root, "regist_date", "release_date", "on_sale_date", "regist_date_string");
            work.ReleaseDate = IGalInfoPhraser.GetDateTimeFromString(dateStr)
                               ?? ParseLooseDate(dateStr);

            foreach (var g in ReadStringArray(root, "genre", "genres", "genre_string"))
                work.Genres.Add(g);

            // 实测：顶层的 voice_by / scenario_by / illust_by / music_by **恒为 null**（历史字段），
            // 真正的名单在 creaters 里，形状是 { role_key: [ {id,name,classification,…}, … ] }。
            // 也有作品是空数组（确实没登记 staff）——那种情况交给 HTML 的「声優 / 音楽」行兜底，
            // 这正是 FetchWorkAsync 两个来源都要抓的原因。
            if (root.TryGetProperty("creaters", out var creaters) &&
                creaters.ValueKind == JsonValueKind.Object)
            {
                foreach (var v in ReadStringArray(creaters, "voice_by", "seiyu", "voice_actors"))
                    work.VoiceActors.Add(v);
                foreach (var v in ReadStringArray(creaters, "illust_by", "illustrator", "illust"))
                    work.Illustrators.Add(v);
                foreach (var v in ReadStringArray(creaters, "scenario_by", "scenario", "writer"))
                    work.ScenarioWriters.Add(v);
                foreach (var v in ReadStringArray(creaters, "music_by", "music", "musician"))
                    work.Musicians.Add(v);
            }

            var priceStr = ReadString(root, "price", "price_yen", "official_price");
            if (int.TryParse(Regex.Match(priceStr ?? "", @"\d+").Value, out var price))
                work.PriceYen = price;

            return work;
        }
        catch (JsonException)
        {
            return null; // 不是 JSON（比如返回了 HTML 错误页）
        }
    }

    /// <summary>
    /// 读 DLsite 的图片对象：形如 <c>"image_main": { …, "url": "//img.dlsite.jp/modpub/…" }</c>。
    /// 返回的是协议相对 URL，由 <see cref="Absolutize"/> 补成 https。
    /// </summary>
    private static string? ReadImageUrl(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Object) continue;
            var url = ReadString(v, "url", "resize_url");
            if (url is not null) return url;
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v)) continue;
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement obj, params string[] keys)
    {
        var result = new List<string>();
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v)) continue;

            if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        // 形如 [{"name": "..."}]
                        var n = ReadString(item, "name", "value");
                        if (n is not null) result.Add(n);
                    }
                }
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                // 形如 "カテゴリA / カテゴリB"
                result.AddRange(v.GetString()!
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        return result;
    }

    // =====================================================================
    // HTML 解析（未验证）
    // =====================================================================

    private async Task<DLsiteWork?> ParseHtmlAsync(string html, string productId, string section)
    {
        // 年龄确认页判定：出现「年齢確認」且**没有** og:title。
        // 只用「年齢確認」判断会误伤正常作品页（页面上也可能有这个词），所以必须有第二个条件。
        if (html.Contains("年齢確認", StringComparison.Ordinal) &&
            !html.Contains("og:title", StringComparison.OrdinalIgnoreCase))
        {
            Log($"DLsite 返回了年龄确认页（{productId}），Cookie 可能没生效。");
            return null;
        }

        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var work = new DLsiteWork { ProductId = productId, Section = section };

        // ---- 1) og: 元标签：最稳定的一层 ----
        work.Title = MetaContent(document, "og:title");
        work.Description = MetaContent(document, "og:description");
        work.CoverUrl = MetaContent(document, "og:image");

        // og:title 常见形如「作品名 | DLsite」，去掉后缀
        if (work.Title is not null)
        {
            var idx = work.Title.LastIndexOf('|');
            if (idx > 0) work.Title = work.Title[..idx].Trim();
        }

        // ---- 2) 按表头文字扫描规格表：不依赖 class 名 ----
        var specs = BuildLabelMap(document);

        work.Circle ??= CleanCircle(FirstOf(specs, LabelCircle, LabelBrand));
        work.Genres.AddRange(SplitGenre(FirstOf(specs, LabelGenre)));
        work.SeriesName ??= FirstOf(specs, LabelSeries);
        work.WorkType ??= FirstOf(specs, LabelWorkType);
        work.AgeRating ??= FirstOf(specs, LabelAgeRating);

        var releaseRaw = FirstOf(specs, LabelReleaseDate, LabelReleaseDateAlt);
        work.ReleaseDate ??= ParseLooseDate(releaseRaw);

        var priceRaw = FirstOf(specs, LabelPrice);
        if (work.PriceYen is null && priceRaw is not null)
        {
            var digits = Regex.Match(priceRaw, @"[\d,]+").Value.Replace(",", "");
            if (int.TryParse(digits, out var p)) work.PriceYen = p;
        }

        work.VoiceActors.AddRange(SplitStaff(FirstOf(specs, LabelVoiceActor)));
        work.Illustrators.AddRange(SplitStaff(FirstOf(specs, LabelIllustrator)));
        work.ScenarioWriters.AddRange(SplitStaff(FirstOf(specs, LabelScenario)));
        work.Musicians.AddRange(SplitStaff(FirstOf(specs, LabelMusic)));

        // ---- 3) 兜底：标题还空就找 h1 ----
        work.Title ??= document.QuerySelector("h1")?.TextContent?.Trim();

        // ---- 4) 兜底：封面还空就找作品区里的第一张图 ----
        work.CoverUrl ??= document.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                          ?? document.QuerySelector("img[src*='/product/']")?.GetAttribute("src");

        NormalizeUrls(work);
        return work;
    }

    private static string? MetaContent(IDocument document, string property) =>
        document.QuerySelector($"meta[property='{property}']")?.GetAttribute("content")
        ?? document.QuerySelector($"meta[name='{property}']")?.GetAttribute("content");

    /// <summary>
    /// 扫描页面里的 <c>th</c>，按表头文字建立「标签 → 该行单元格的文字」映射。
    /// 这样即使 DLsite 改 class / 改 id，只要表头文字不变就还能用。
    /// </summary>
    private static Dictionary<string, string> BuildLabelMap(IDocument document)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var th in document.QuerySelectorAll("th"))
        {
            var label = th.TextContent?.Trim();
            if (string.IsNullOrEmpty(label)) continue;

            var td = th.NextElementSibling;
            if (td is null) continue;

            var value = td.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;

            // 同一个表头出现多次时保留第一个非空的
            map.TryAdd(label, Regex.Replace(value, @"\s+", " "));
        }

        return map;
    }

    /// <summary>
    /// 按标签取值。先精确匹配；表头偶尔会带「：」或额外说明，所以再退化为「包含」匹配。
    /// </summary>
    private static string? FirstOf(Dictionary<string, string> map, params string[] labels)
    {
        foreach (var label in labels)
        {
            if (map.TryGetValue(label, out var exact)) return exact;
        }

        foreach (var label in labels)
        {
            foreach (var (key, value) in map)
            {
                if (key.Contains(label, StringComparison.Ordinal)) return value;
            }
        }

        return null;
    }

    /// <summary>
    /// 拆「ジャンル」：实测是**空格分隔**（`おっぱい 着衣 フェチ …`），而标签本身会带斜杠
    /// （`学校/学園`、`巨乳/爆乳`）—— 所以这里**只按空白拆，绝不能按 / 拆**。
    /// </summary>
    private static IEnumerable<string> SplitGenre(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        char[] separators = [' ', '\u3000'];
        foreach (var part in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0) yield return part;
        }
    }

    /// <summary>
    /// 拆 staff 名字：实测是「<b>空格 / 空格</b>」分隔（`赤月ゆむ / 乙倉ゆい / …`）。
    /// 但括号里也可能有斜杠 —— `でらうえあ(原画 / SD原画)` 是**同一个人** ——
    /// 所以只在**括号深度为 0** 的地方按 / 拆，另外兼容 、，, 三种写法。
    /// </summary>
    private static IEnumerable<string> SplitStaff(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        var buf = new StringBuilder();
        var depth = 0;
        foreach (var ch in raw)
        {
            if (ch is '(' or '（') depth++;
            else if (ch is ')' or '）') depth = Math.Max(0, depth - 1);

            if (depth == 0 && ch is '/' or '、' or '，' or ',')
            {
                var part = buf.ToString().Trim();
                if (part.Length > 0) yield return part;
                buf.Clear();
                continue;
            }

            buf.Append(ch);
        }

        var last = buf.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>
    /// HTML 的「ブランド名」单元格里混着页面 UI 文案（实测尾部是「フォローする」），去掉。
    /// 有 JSON 的 maker_name 时本来轮不到这里，这是 JSON 没给社团时的兜底。
    /// </summary>
    private static string? CleanCircle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var s = raw.Replace("フォローする", string.Empty, StringComparison.Ordinal);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 0 ? s : raw;
    }

    /// <summary>宽松日期解析：<c>yyyy-MM-dd</c> / <c>yyyy/MM/dd</c> / <c>yyyy年M月d日</c>。</summary>
    private static DateTime? ParseLooseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var m = Regex.Match(raw, @"(\d{4})\D{0,3}(\d{1,2})\D{0,3}(\d{1,2})");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var y) &&
            int.TryParse(m.Groups[2].Value, out var mo) &&
            int.TryParse(m.Groups[3].Value, out var d))
        {
            try { return new DateTime(y, mo, d); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    /// <summary>把相对 URL 补成绝对 URL（og:image 一般是绝对的，兜底路径可能不是）。</summary>
    private static void NormalizeUrls(DLsiteWork work)
    {
        work.CoverUrl = Absolutize(work.CoverUrl);
        work.HeaderUrl = Absolutize(work.HeaderUrl);
    }

    private static string? Absolutize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (url.StartsWith('/')) return "https://www.dlsite.com" + url;
        return url;
    }

    /// <summary>
    /// 清掉标题尾巴上的促销文案。实测常见形如
    /// 「作品名【クーポン利用で20%OFF！】（9/24 23:59まで）」，8 个样本里 6 个带。
    /// <para>
    /// 规则刻意收窄：只删「以 クーポン 开头的【…】」以及紧跟其后的「（…）」，
    /// 所以标题里正常的括号（比如「（前編）」）不受影响。
    /// 不想要这个清理的话，把 <c>FetchWorkAsync</c> 里那行调用删掉即可，标题就原样保留站点文案。
    /// </para>
    /// </summary>
    private static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var s = Regex.Replace(raw, @"\s*【クーポン[^】]*】\s*(（[^）]*）)?\s*$", string.Empty).Trim();
        return s.Length > 0 ? s : raw;
    }

    // =====================================================================
    // 名称搜索兜底
    // =====================================================================

    /// <summary>
    /// 依次用「日文原名 → 显示名 → 中文名」去各分区搜，取第一个足够像的结果。
    /// <para>
    /// ⚠️ 顺序很关键：PotatoVN 库里 <c>Name</c> 往往是**中文译名**，
    /// 真正的**日文原名**在同一条记录的
    /// <c>OriginalName</c> 里（「テスト用の長いタイトル～サブタイトルつき～」）。
    /// DLsite 的搜索索引只有日文/英文标题 —— 拿中文名去搜是 **0 命中**，所以必须先试
    /// <c>OriginalName</c>，第一版只用 <c>Name</c> 正是自动搜索用不了的原因。
    /// </para>
    /// <para>
    /// 只有中文名（刚拖进库、还没刮过的游戏）时搜不到，这是 DLsite 索引本身决定的，没有别的源可用
    /// 这类情况只能靠先用内置源刮一次拿到日文原名。
    /// 想让这类游戏自动搜到，先用内置源（Bangumi 等）刮一次 —— 宿主会把日文原名写进
    /// <c>OriginalName</c>，之后本插件就能自动搜到了。
    /// </para>
    /// </summary>
    private async Task<string?> TryFindProductIdByNameAsync(Galgame galgame)
    {
        var keywords = SearchKeywords(galgame).ToList();

        // 打分用完整原名（截短词只负责把结果捞回来，不参与打分）
        var reference = FullNameForScoring(galgame);

        // maniax 先跑完所有关键词：库里绝大多数是同人作品，这样常见情况 2~3 个请求就命中
        //（实测 DLsite 对连续请求会 403，请求数得省着用）。
        foreach (var keyword in keywords)
        {
            var id = await SearchInSectionAsync("maniax", keyword, reference);
            if (id is not null) return id;
        }

        // 其余分区再逐个试：搜索是按分区隔离的，拿 maniax 搜 pro 的作品会搜不到。
        foreach (var section in Sections.Where(s => !string.Equals(s, "maniax", StringComparison.Ordinal)))
        {
            foreach (var keyword in keywords)
            {
                var id = await SearchInSectionAsync(section, keyword, reference);
                if (id is not null) return id;
            }
        }

        // 都没搜到：说清楚试过哪些词（不要静默失败）
        Log($"DLsite 各分区都没搜到（试过：{string.Join("、", keywords)}）。若是刚拖进库的游戏，可能是它只有中文名 —— " +
            $"先用内置源刮一次拿到日文原名，或者直接把 RJ 号填进本插件的 id 栏。");
        return null;
    }

    /// <summary>搜索词最短长度：太短会命中一大片无关作品，纯浪费请求。</summary>
    private const int MinKeywordLength = 4;

    /// <summary>最多用几个搜索词（DLsite 连续请求会被 403，得有上限）。</summary>
    private const int MaxSearchKeywords = 4;

    private static readonly char[] WhitespaceCut = [' ', '\u3000', '\t'];

    /// <summary>副标题分隔符。这些后面的内容常常对不上 DLsite 的索引，是搜索失败的元凶。</summary>
    private static readonly char[] SubtitleCut = ['～', '〜', '~', '（', '(', '【', '[', '『', '｜', '|', '：', ':'];

    /// <summary>
    /// 候选搜索词：日文原名优先，其次显示名与中文名（两者常常相同）；
    /// 并且对每个名字**派生截短变体**。
    ///
    /// <para>
    /// 为什么必须截短（实测，2026-09）：DLsite 的 <c>fsr</c> 搜索要求「所有分词都命中」，
    /// 于是长标题一旦带空格 + <c>～副标题～</c> 就整个 0 结果：
    /// </para>
    /// <list type="bullet">
    /// <item>长标题（带空格 + ～副标题～）完整使用 → <b>0 个作品</b></item>
    /// <item>同一标题截到第一个空格 → <b>能搜到</b> ✓</item>
    /// <item>没有空格的长标题 → 完整名字本来就能搜到 ✓</item>
    /// </list>
    /// <para>
    /// 反例：<c>テスト用の長いタイトル～サブタイトルつき～</c>（没有空格）用完整名字就能搜到，
    /// 所以「完整名字」永远排第一，只有它失败才用变体。
    /// </para>
    /// </summary>
    internal static IEnumerable<string> SearchKeywords(Galgame galgame)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var raw in new[]
                 {
                     galgame.OriginalName?.Value,
                     galgame.Name?.Value,
                     galgame.ChineseName?.Value,
                 })
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            foreach (var variant in Variants(name!))
            {
                if (variant.Length < MinKeywordLength) continue;
                if (seen.Add(variant)) result.Add(variant);
            }
        }

        return result.Take(MaxSearchKeywords);
    }

    /// <summary>名字的搜索变体：完整 → 截到第一个空白 → 去尾部符号 → 截到第一个副标题分隔符（各带去符号版）。</summary>
    private static IEnumerable<string> Variants(string name)
    {
        yield return name;

        var head = CutAt(name, WhitespaceCut);
        if (!string.Equals(head, name, StringComparison.Ordinal))
        {
            yield return head;
            yield return TrimTailSymbols(head);
        }

        var sub = CutAt(name, SubtitleCut);
        if (!string.Equals(sub, name, StringComparison.Ordinal) &&
            !string.Equals(sub, head, StringComparison.Ordinal))
        {
            yield return sub;
            yield return TrimTailSymbols(sub);
        }
    }

    /// <summary>截到第一个分隔符之前（分隔符在开头就返回原串）。</summary>
    private static string CutAt(string s, char[] separators)
    {
        var at = s.IndexOfAny(separators);
        return (at > 0 ? s[..at] : s).Trim();
    }

    /// <summary>去掉结尾的空白/标点/符号（<c>♪！?～</c> 这类）。</summary>
    private static string TrimTailSymbols(string s)
    {
        var end = s.Length;
        while (end > 0)
        {
            var c = s[end - 1];
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)) break;
            end--;
        }

        return s[..end].Trim();
    }

    /// <summary>
    /// 候选作品与搜索词的匹配度。
    /// <para>
    /// ⚠️ 打分**不能**用那个截短的搜索词：同系列作品的开头往往完全一样，
    /// 否则两边会拿到同一个分数，平局按页面顺序取，
    /// 结果就是把同系列的续作当成本篇抓了回来。
    /// 拿截短词给两边打分都是一个分数，平局就按页面顺序取，结果抓错了作品。
    /// </para>
    /// <para>
    /// 所以：<b>完整原名</b>当主判据（<paramref name="reference"/>），截短词只在前者匹配不上时给个及格分
    /// （<see cref="PrefixFloorScore"/>，刚好过阈值、又低于真正匹配上的分数）。
    /// </para>
    /// </summary>
    private static double ScoreCandidate(string keyword, string title, string? reference)
    {
        var target = string.IsNullOrWhiteSpace(reference) ? keyword : reference!;

        var score = NormalizeForMatch(target) == NormalizeForMatch(title)
            ? 1.0
            : IGalInfoPhraser.Similarity(target, title);

        var k = NormalizeForMatch(keyword);
        var t = NormalizeForMatch(title);
        if (k.Length >= 8 && t.StartsWith(k, StringComparison.Ordinal))
            score = Math.Max(score, PrefixFloorScore);

        return score;
    }

    /// <summary>截短搜索词的兜底分：高于阈值所以能被采用，但低于"完整名匹配"（1.0 附近），不会抢走正确的那个。</summary>
    private const double PrefixFloorScore = 0.80;

    /// <summary>
    /// 打分用的「完整原名」：日文原名优先，其次是显示名/中文名。
    /// 只用原始字段，不用 <see cref="Variants"/> 派生出来的截短词。
    /// </summary>
    private static string? FullNameForScoring(Galgame galgame) =>
        new[] { galgame.OriginalName?.Value, galgame.Name?.Value, galgame.ChineseName?.Value }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string NormalizeForMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return new string(s
                .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
                .ToArray())
            .ToLowerInvariant();
    }

    private async Task<string?> SearchInSectionAsync(string section, string keyword, string? reference)
    {
        var html = await GetStringAsync(SearchUrl(section, keyword));
        if (html is null) return null;

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = await context.OpenAsync(req => req.Content(html));

            var candidates = new List<(string Id, string Title, double Score)>();

            foreach (var a in document.QuerySelectorAll("a[href*='product_id/']"))
            {
                var href = a.GetAttribute("href");
                if (href is null) continue;

                var m = Regex.Match(href, @"product_id/([A-Za-z]{2}\d{6,8})");
                if (!m.Success) continue;

                var id = m.Groups[1].Value.ToUpperInvariant();
                var title = a.TextContent?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;

                var score = ScoreCandidate(keyword, title, reference);
                candidates.Add((id, title, score));
            }

            if (candidates.Count == 0) return null;

            var best = candidates.OrderByDescending(c => c.Score).First();

            // 相似度太低宁可不猜，避免把错误的作品信息写进库
            if (best.Score < MinSearchScore)
            {
                Log($"按「{keyword}」在 {section} 搜到的 DLsite 候选相似度最高只有 {best.Score:F2}，放弃。");
                return null;
            }

            return best.Id;
        }
        catch (Exception e)
        {
            Log($"DLsite 名称搜索解析失败（{section} / {keyword}）：{e.Message}");
            return null;
        }
    }

    // =====================================================================
    // 映射到 PotatoVN 模型
    // =====================================================================

    private Galgame ToGalgame(DLsiteWork work, Galgame source)
    {
        var result = new Galgame
        {
            // ⚠️ 必须先设 RssType 再设 Id：
            // Galgame.Id 的 setter 会看 RssType 决定写进 Ids[] 还是 IdForPlugins。
            // 顺序反了会把 DLsite 的 id 写进内置源（Vndb）那个槽位。
            RssType = (RssType)Plugin.ParserId,
            Id = work.ProductId,

            Name = work.Title ?? source.Name.Value ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(work.Description))
            result.Description = work.Description;

        if (!string.IsNullOrWhiteSpace(work.Circle))
            result.Developer = work.Circle;

        if (work.ReleaseDate is { } date)
            result.ReleaseDate = date;

        if (!string.IsNullOrWhiteSpace(work.CoverUrl))
            result.ImageUrl = work.CoverUrl;

        if (!string.IsNullOrWhiteSpace(work.HeaderUrl))
            result.HeaderImageUrl = work.HeaderUrl;

        if (work.Genres.Count > 0)
            result.Tags.Value = new ObservableCollection<string>(work.Genres.Distinct());

        return result;
    }

    private static DLsiteWork? Merge(DLsiteWork? a, DLsiteWork? b)
    {
        if (a is null) return b;
        if (b is null) return a;

        a.Title ??= b.Title;
        a.Circle ??= b.Circle;
        a.Description ??= b.Description;
        a.CoverUrl ??= b.CoverUrl;
        a.HeaderUrl ??= b.HeaderUrl;
        a.SeriesName ??= b.SeriesName;
        a.WorkType ??= b.WorkType;
        a.AgeRating ??= b.AgeRating;
        a.PriceYen ??= b.PriceYen;
        a.ReleaseDate ??= b.ReleaseDate;

        foreach (var g in b.Genres) if (!a.Genres.Contains(g)) a.Genres.Add(g);
        foreach (var v in b.VoiceActors) if (!a.VoiceActors.Contains(v)) a.VoiceActors.Add(v);
        foreach (var v in b.Illustrators) if (!a.Illustrators.Contains(v)) a.Illustrators.Add(v);
        foreach (var v in b.ScenarioWriters) if (!a.ScenarioWriters.Contains(v)) a.ScenarioWriters.Add(v);
        foreach (var v in b.Musicians) if (!a.Musicians.Contains(v)) a.Musicians.Add(v);

        return a;
    }

    // =====================================================================
    // 封面 / 头图
    // =====================================================================

    public async Task<List<string>> GetGalCoversAsync(Galgame galgame)
    {
        var work = await LoadWorkAsync(galgame);
        return work?.CoverUrl is { Length: > 0 } url ? [url] : [];
    }

    public async Task<List<string>> GetGalHeadersAsync(Galgame galgame)
    {
        var work = await LoadWorkAsync(galgame);
        return work?.HeaderUrl is { Length: > 0 } url ? [url] : [];
    }

    private async Task<DLsiteWork?> LoadWorkAsync(Galgame galgame)
    {
        var id = GetProductId(galgame);
        if (string.IsNullOrWhiteSpace(id)) id = await TryFindProductIdByNameAsync(galgame);
        if (string.IsNullOrWhiteSpace(id)) return null;
        return await FetchWorkAsync(id);
    }

    // =====================================================================
    // staff（声優 / イラスト / シナリオ / 音楽）
    // =====================================================================

    /// <summary>
    /// 返回本作的所有 staff 及职位。DLsite 的作品页本身就有这些栏目，不需要额外请求。
    /// <para>
    /// 注意：DLsite 把「声優」「イラスト」等作为栏目标题，值是一串人名。
    /// 这里把它们转成 <see cref="StaffRelation"/>；同一人担任多个职位会分别出现，宿主会自动合并。
    /// </para>
    /// </summary>
    public async Task<List<StaffRelation>> GetStaffsAsync(Galgame game)
    {
        var result = new List<StaffRelation>();

        try
        {
            var work = await LoadWorkAsync(game);
            if (work is null) return result;

            AddStaff(result, work.VoiceActors, Career.Seiyu);
            AddStaff(result, work.Illustrators, Career.Painter);
            AddStaff(result, work.ScenarioWriters, Career.Writer);
            AddStaff(result, work.Musicians, Career.Musician);
        }
        catch (Exception e)
        {
            // 接口文档要求：内部不捕获异常，调用方需捕获。
            // 但这里已经是我们自己的最外层，记日志后返回空列表，避免影响搜刮其它字段。
            Log($"DLsite staff 解析失败：{e.Message}");
        }

        return result;
    }

    private static void AddStaff(List<StaffRelation> target, List<string> names, Career career)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var staff = new StaffRelation();
            // ⚠️ 不要写 staff.Ids[(int)Plugin.ParserId]：Staff.Ids 只有 Galgame.PhraserNumber(9) 个元素，
            // 插件 id(>100) 会越界。插件的 staff id 没有合适的存放位置，这里只填名字。
            staff.JapaneseName = name;
            staff.Career.Add(career);
            staff.Relation.Add(career);
            target.Add(staff);
        }
    }

    /// <summary>
    /// 获取单个 staff 的详细信息。
    /// <para>
    /// DLsite 没有可供插件查询的「人物详情」入口，所以这里按接口约定返回 null
    /// （文档明确写着「若搜刮失败返回null」，调用方会接受）。
    /// </para>
    /// </summary>
    public Task<Staff?> GetStaffAsync(Staff staff) => Task.FromResult<Staff?>(null);

    // =====================================================================
    // 日志
    // =====================================================================

    private static void Log(string msg)
    {
        try
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, $"[DLsite] {msg}");
        }
        catch
        {
            // 插件可能还没初始化完，忽略
        }
    }
}
