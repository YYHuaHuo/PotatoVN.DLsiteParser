using GalgameManager.Enums;
using GalgameManager.Models;

namespace GalgameManager.ViewModels;

/// <summary>
/// 照 PotatoVN 1.10.2 的 <c>GalgameSettingViewModel</c> 造一个**同形状的假页面**：
/// 命名空间与类名跟真身一致（搜索链接补丁是按名字找类型的），成员只留补丁用得到的那些。
/// </summary>
public class GalgameSettingViewModel
{
    private RssType _selectedRss;

    /// <summary>与 <c>[ObservableProperty]</c> 生成的 setter 同形：值一改就喊 OnSelectedRssChanged。</summary>
    public RssType SelectedRss
    {
        get => _selectedRss;
        set
        {
            _selectedRss = value;
            OnSelectedRssChanged(value);
        }
    }

    /// <summary>与 PotatoVN 里的默认值一致（默认指向 VNDB）。</summary>
    public string SearchUri { get; set; } = "https://vndb.org/v/all?sq=";

    public Galgame Gal { get; set; } = new();

    // 宿主里这段是 [ObservableProperty] 生成的 partial 方法；这里要有实体，
    // 否则编译器会把 setter 里的调用点优化掉，Harmony 也就无从下手。
    private void OnSelectedRssChanged(RssType value)
    {
    }
}