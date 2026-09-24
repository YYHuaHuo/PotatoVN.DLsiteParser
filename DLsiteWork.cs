using System;
using System.Collections.Generic;

namespace PotatoVN.App.DLsiteParser;

/// <summary>
/// DLsite 一个作品的原始解析结果。
/// <para>
/// 这一层刻意与 PotatoVN 的 <c>Galgame</c> 解耦：解析器只负责把页面/接口读成这个对象，
/// 再由 <see cref="DLsitePhraser"/> 转成 <c>Galgame</c>。
/// 这样站点改版时只需改解析，不用碰映射逻辑。
/// </para>
/// </summary>
public sealed class DLsiteWork
{
    /// <summary>作品 id，如 <c>RJ123456</c> / <c>VJ123456</c> / <c>BJ123456</c>。</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>作品页面所在分区，如 <c>maniax</c> / <c>pro</c> / <c>girls</c> / <c>soft</c>。</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>作品名（日文原名）。</summary>
    public string? Title { get; set; }

    /// <summary>サークル名（同人）或 ブランド名（商业）→ 映射为 Developer。</summary>
    public string? Circle { get; set; }

    /// <summary>发售日。</summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>作品简介 / あらすじ → 映射为 Description。</summary>
    public string? Description { get; set; }

    /// <summary>封面图 URL。</summary>
    public string? CoverUrl { get; set; }

    /// <summary>横版/头图 URL（DLsite 的 <c>*/img_main.jpg</c> 之类），没有则为 null。</summary>
    public string? HeaderUrl { get; set; }

    /// <summary>ジャンル（标签）→ 映射为 Tags。</summary>
    public List<string> Genres { get; } = [];

    /// <summary>シリーズ名。</summary>
    public string? SeriesName { get; set; }

    /// <summary>作品形式，如「アドベンチャー」「音声作品」。</summary>
    public string? WorkType { get; set; }

    /// <summary>价格（日元），解析不到则为 null。</summary>
    public int? PriceYen { get; set; }

    /// <summary>年龄指定，如「R18」「全年齢」。</summary>
    public string? AgeRating { get; set; }

    /// <summary>声優。</summary>
    public List<string> VoiceActors { get; } = [];

    /// <summary>イラスト / 原画。</summary>
    public List<string> Illustrators { get; } = [];

    /// <summary>シナリオ。</summary>
    public List<string> ScenarioWriters { get; } = [];

    /// <summary>音楽。</summary>
    public List<string> Musicians { get; } = [];

    /// <summary>
    /// 是否解析到了至少一项可用信息。
    /// <para>
    /// 用于站点改版时的兜底：全部关键字段都空说明解析失效，此时应返回 null 而不是把空对象写进游戏库。
    /// </para>
    /// </summary>
    public bool HasAnyUsefulData =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Circle)
        || !string.IsNullOrWhiteSpace(Description)
        || !string.IsNullOrWhiteSpace(CoverUrl)
        || Genres.Count > 0
        || ReleaseDate is not null;
}
