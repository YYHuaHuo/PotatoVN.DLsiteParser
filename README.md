# PotatoVN.App.DLsiteParser

一个 PotatoVN 插件：为 PotatoVN 增加一个 **DLsite** 游戏信息搜刮器。

通过 DLsite 的作品 id（`RJ123456` / `VJ123456` / `BJ123456` 等）抓取作品信息并写入游戏条目：
标题（日文原名）、社团（Developer）、发售日、标签、封面、简介，以及 staff（声优 / 原画 / 剧本 / 音乐）。

- 覆盖分区：`maniax` / `pro` / `soft` / `girls` / `comic` / `home`
- 数据来源：DLsite 的 JSON 接口 + 作品页 HTML，双路抓取后合并（JSON 字段名与直觉不符，HTML 补它缺的）
- 网络：只访问 `dlsite.com`
- 抓不到不会静默失败：日志会写明试过哪些关键词、哪些分区，以及怎么自查

---

## 关键标识

| 项 | 值 | 说明 |
| --- | --- | --- |
| 插件 GUID | `8738f51b-0217-49d1-a3a4-50e9411feb48` | 必须永久不变；与 `AssemblyName` 绑定 |
| `AssemblyName` | `A8738f51b-0217-49d1-a3a4-50e9411feb48` | 命名规则 `A{GUID}`，避免与其他插件程序集冲突 |
| `ParserId` | `769164` | 必须 **> 100** 且全局唯一（`Galgame.IdForPlugins` 用这个 key） |
| 插件市场 `types` | `4` | `Parser`（搜刮器） |

---

## 目录结构

```
PotatoVN.App.DLsiteParser/
├─ PotatoVN.App.DLsiteParser.csproj   # TFM / AssemblyName / namespace stamping / PackPlugin 打包目标
├─ Plugin.cs                          # 插件主类：IPlugin + IParserProvider
├─ DLsitePhraser.cs                   # 搜刮器本体：IGalInfoPhraser + 封面 / staff
├─ DLsiteWork.cs                      # DLsite 作品数据模型
├─ harness/                           # 离线自检控制台（自带 Main，不编进插件）
└─ README.md
```

编译需要 PotatoVN 主仓库的公开库 `GalgameManager.WinApp.Base`（通过 git submodule 引入），
`csproj` 里的 `ProjectReference` 指向 `..\PotatoVN\GalgameManager.WinApp.Base\...`。

---

## 使用

在游戏详情页把信息源选成本插件，填 DLsite 作品号后点「从信息源获取」。
没有 id 时按三级退：

1. **按名字搜**：日文原名 -> 显示名 -> 中文名，并对每个名字派生截短变体；先 maniax 再其他分区，
   取相似度最高的候选
2. 全搜不到 -> 返回 null，并在日志里写明「各分区都没搜到（试过：...）」+ 自查建议
3. 不静默失败，也不会把错误的作品写进库（相似度低于 0.75 一律放弃）
