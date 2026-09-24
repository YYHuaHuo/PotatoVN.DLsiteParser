using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using PotatoVN.App.DLsiteParser;

namespace DLsiteHarness;

/// <summary>
/// 直接调用插件里的 <see cref="DLsitePhraser"/>，对真实 DLsite 跑一遍搜刮并打印结果。
/// 不经过 PotatoVN，因此可以在装进应用之前就把"能不能解析出东西"验掉。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 把宿主桩挂上：插件通过 HostApi.Log 发出的「为什么失败 / 反查到了什么」就能看见
        await new Plugin().InitializeAsync(HostApiProxy.Create());

        // 诊断模式：只读看一眼 PotatoVN 的游戏库，确认「日文原名」有没有被填过
        //（自动搜索能不能成立，全看这个字段）。
        if (args.Length > 0 && args[0] == "--db-info")
        {
            DumpGalgameDb(args.Length > 1 ? args[1] : null);
            return 0;
        }

        // 自动搜索模式：模拟应用里"游戏没填 DLsite id，让插件自己搜"的情形。
        //   --search "<日文原名>" ["<显示名(中文)>"]
        // 只给日文原名时，显示名也填成同一个；给两个参数就能复现"Name 是中文、OriginalName 是日文"。
        if (args.Length > 1 && args[0] == "--search")
        {
            return await SearchByNameAsync(args[1], args.Length > 2 ? args[2] : args[1]);
        }

        // 搜索词变体模式：纯离线，验「长标题怎么截」（DLsite fsr 搜索要求所有分词都命中的坑）
        if (args.Length > 0 && args[0] == "--keywords")
        {
            return KeywordVariantsTest();
        }

        // 默认三个样本（**占位作品号**，跑之前换成你要验的那些），覆盖三种情况：
        //   VJ00000001  商业作品，staff 齐全（声優/イラスト/シナリオ/音楽 都在页面上）
        //   VJ00000002  商业作品，JSON 的 creaters 是空数组（只能靠 HTML 的 staff 行）
        //   RJ00000001  同人作品，只有 シナリオ/イラスト
        var ids = args.Length > 0 ? args : ["VJ00000001", "VJ00000002", "RJ00000001"];

        using var phraser = new DLsitePhraser();
        Console.WriteLine($"ParserId={Plugin.ParserId}  PhraseType={phraser.GetPhraseType()}");
        Console.WriteLine();

        foreach (var id in ids)
        {
            Console.WriteLine($"===== {id} =====");

            var source = new Galgame { RssType = (RssType)Plugin.ParserId };
            source.IdForPlugins[Plugin.ParserId] = id;

            Galgame? info;
            try
            {
                info = await phraser.GetGalgameInfo(source);
            }
            catch (Exception e)
            {
                Console.WriteLine($"  抛异常: {e.GetType().Name}: {e.Message}");
                Console.WriteLine();
                continue;
            }

            if (info is null)
            {
                Console.WriteLine("  结果: null（解析失败）");
                Console.WriteLine();
                continue;
            }

            var covers = await phraser.GetGalCoversAsync(info);
            var staff = await phraser.GetStaffsAsync(info);
            ObservableCollection<string>? tags = info.Tags?.Value;

            Console.WriteLine($"  标题   : {T(info.Name?.Value)}");
            Console.WriteLine($"  社团   : {T(info.Developer?.Value)}");
            Console.WriteLine($"  发售日 : {info.ReleaseDate?.Value:yyyy-MM-dd}");
            Console.WriteLine($"  标签   : {tags?.Count ?? 0} 个  [{T(tags is null ? null : string.Join(" / ", tags), 88)}]");
            Console.WriteLine($"  封面   : {T(info.ImageUrl, 78)}");
            Console.WriteLine($"  头图   : {T(info.HeaderImageUrl, 78)}");
            Console.WriteLine($"  简介   : {info.Description?.Value?.Length ?? 0} 字  [{T(info.Description?.Value, 36)}]");
            Console.WriteLine($"  封面源 : {covers.Count} 张");
            Console.WriteLine($"  staff  : {staff.Count} 人  [{string.Join(",", staff.Select(s => s.Career.FirstOrDefault()).Distinct())}]");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// 模拟应用里的自动搜索：构造一个**没有 DLsite id** 的游戏，只给名字，
    /// 看插件能不能自己搜到作品并抓回信息。
    /// </summary>
    private static async Task<int> SearchByNameAsync(string originalName, string displayName)
    {
        Console.WriteLine("模拟自动搜索（无 id）");
        Console.WriteLine($"  显示名 Name         : {displayName}");
        Console.WriteLine($"  日文原名 OriginalName: {originalName}");
        Console.WriteLine();

        var game = new Galgame
        {
            RssType = (RssType)Plugin.ParserId,
            Name = displayName,
            OriginalName = originalName,
        };

        using var phraser = new DLsitePhraser();
        var info = await phraser.GetGalgameInfo(game);

        if (info is null)
        {
            Console.WriteLine("  结果: null —— 没搜到（应用里就表现为「自动搜索没反应」）");
            return 1;
        }

        Console.WriteLine($"  搜到 id: {info.Id}");
        Console.WriteLine($"  标题   : {T(info.Name?.Value)}");
        Console.WriteLine($"  社团   : {T(info.Developer?.Value)}");
        Console.WriteLine($"  发售日 : {info.ReleaseDate?.Value:yyyy-MM-dd}");
        Console.WriteLine($"  封面   : {T(info.ImageUrl, 78)}");
        return 0;
    }

    /// <summary>
    /// 只读看一眼 PotatoVN 的游戏库（LiteDB），确认每款游戏有没有「日文原名」。
    /// <para>
    /// 为什么关心这个：实测**中文译名在 DLsite 搜索里 0 命中**（DLsite 的索引只有日文/英文标题），
    /// 而库里那些游戏的目录名又都不含 RJ 号，所以"自动搜到"唯一能靠的就是日文原名。
    /// </para>
    /// </summary>
    private static void DumpGalgameDb(string? explicitPath = null)
    {
        var path = explicitPath ?? FindPotatoVnDb();
        if (path is null)
        {
            Console.WriteLine("没找到 PotatoVN 的库文件（pvn_data.db）。");
            Console.WriteLine(@"可以手动指定：dlsite-harness.dll --db-info ""%LOCALAPPDATA%\Packages\<包族名>\LocalState\pvn_data.db""");
            return;
        }
        Console.WriteLine($"库文件: {path}");
        Console.WriteLine($"存在: {File.Exists(path)}");
        if (!File.Exists(path)) return;

        using var db = new LiteDB.LiteDatabase(
            new LiteDB.ConnectionString { Filename = path, ReadOnly = true });

        foreach (var name in db.GetCollectionNames())
        {
            var col = db.GetCollection(name);
            var count = col.Count();
            Console.WriteLine($"\n== collection [{name}]  文档数={count}");
            if (count == 0) continue;

            foreach (var doc in col.FindAll().Take(8))
            {
                var parts = doc.Keys
                    .Where(k => k.Contains("name", StringComparison.OrdinalIgnoreCase))
                    .Select(k =>
                    {
                        var v = doc[k];
                        var s = v.IsDocument ? (v.AsDocument["Value"]?.AsString ?? v.ToString()) : v.ToString();
                        return $"{k}={T(s, 38)}";
                    });
                Console.WriteLine("   " + string.Join("  |  ", parts));
            }
        }
    }

    /// <summary>
    /// 在 %LOCALAPPDATA%\Packages 下找 PotatoVN 的库文件。
    /// 包族名（`发布者.PotatoVN_哈希`）随发布渠道不同，所以按 `*PotatoVN*` 匹配，不写死。
    /// </summary>
    private static string? FindPotatoVnDb()
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return null;
        return Directory.EnumerateDirectories(packages, "*PotatoVN*")
            .Select(d => Path.Combine(d, "LocalState", "pvn_data.db"))
            .FirstOrDefault(File.Exists);
    }

    private static string T(string? s, int n = 56) =>
        string.IsNullOrWhiteSpace(s) ? "(空)" : (s.Length <= n ? s : s[..n] + "…");

    // =====================================================================
    // 搜索词变体（纯离线，不联网）
    // =====================================================================

    /// <summary>
    /// 验「长标题怎么截」。背景是实测：DLsite 的 <c>fsr</c> 搜索要求所有分词都命中，
    /// 完整长标题（带空格 + ～副标题～）会 0 结果，截到第一个空格就能搜到。
    /// </summary>
    private static int KeywordVariantsTest()
    {
        Console.WriteLine("=== 搜索词变体（DLsite fsr 分词坑）===");
        Console.WriteLine();

        var fail = 0;
        void Check(string what, bool ok, string got)
        {
            Console.WriteLine($"  {(ok ? "[ok]" : "[!!]")} {what}: {got}");
            if (!ok) fail++;
        }

        string Show(IEnumerable<string> kws) => string.Join(" | ", kws.Select(k => $"「{k}」"));

        // 1) 实测搜不到的那种标题：带空格 + ～副标题～
        var a = DLsitePhraser.SearchKeywords(new Galgame
        {
            OriginalName = "これはテスト用の長いタイトル ～サブタイトルつき～",
        }).ToList();
        Console.WriteLine($"  变体: {Show(a)}");
        Check("完整名字排第一（先试原样才对）",
            a.Count > 0 && a[0] == "これはテスト用の長いタイトル ～サブタイトルつき～", a[0]);
        Check("派生出「截到第一个空格」的变体",
            a.Contains("これはテスト用の長いタイトル"), Show(a));

        // 2) 没有空格、用完整名字本来就能搜到的标题：原样必须留在第一位
        const string noSpace = "テスト用の長いタイトル～サブタイトルつき～";
        var b = DLsitePhraser.SearchKeywords(new Galgame { OriginalName = noSpace }).ToList();
        Console.WriteLine($"  变体: {Show(b)}");
        Check("无空格标题的原样仍在第一位", b.Count > 0 && b[0] == noSpace, b[0]);
        Check("顺带派生「截到 ～ 之前」的变体", b.Contains("テスト用の長いタイトル"), Show(b));

        // 3) 日文原名优先，且总量有上限（请求数要省着用，DLsite 会 403）
        var c = DLsitePhraser.SearchKeywords(new Galgame
        {
            OriginalName = "べつのテスト用タイトル ～副題つき～",
            Name = "中文显示名",
        }).ToList();
        Console.WriteLine($"  变体: {Show(c)}");
        Check("日文原名优先", c.Count > 0 && c[0].StartsWith("べつのテスト用タイトル", StringComparison.Ordinal), c[0]);
        Check("搜索词总数 <= 4", c.Count <= 4, c.Count.ToString());

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "===== 全部通过 =====" : $"===== 有 {fail} 项不符 =====");
        return fail == 0 ? 0 : 1;
    }

}
