# TarkovMonitor 中文本地化术语表

> 本表约束 `TarkovMonitor/Properties/Strings.zh.resx` 的翻译用词，供后续翻译（如阶段 3/4）与其他语言贡献者参照。
> 英文源串在 `TarkovMonitor/Properties/Strings.resx`；改术语时请同步更新本表与两个 resx，并在第 7 节记录决定。
> 运行时查找走 `LocalizationService.GetString`（Blazor/C# 实例）与 `LocalizationService.T`（静态类），缺失 key 自动回退英文。

## 1. 保留英文的术语

| 英文 | 处理方式 | 备注 |
|---|---|---|
| PMC / PVE / PVP / Seasonal | 不译 | 游戏模式，社区通用（如 "PVE, Regular (PVP), and Seasonal" → "PVE、Regular (PVP) 和 Seasonal"） |
| Scav | 不译 | 如"你的 Scav 已就绪"、"Scav 冷却"、"Scav 声望" |
| Goons | 不译 | 如"上报 Goons" |
| Tarkov Monitor / Tarkov Tracker / TarkovTracker.org / tarkov.dev | 不译 | 品牌名 |
| EFT | 不译 | 缩写；"Escape from Tarkov" 全称译作**《逃离塔科夫》** |
| 地图名（Factory、Customs…） | 由 tarkov.dev API 按语言设置返回中文 | 不在 resx 中维护，无需映射表 |

## 2. 游戏与战局术语

| 英文 | 中文 | 出处 |
|---|---|---|
| Raid | 战局 | 沿用现有 |
| Matching / Match found | 匹配 / 找到战局 | 沿用现有风格 |
| Queue time | 排队时间 | 沿用现有 |
| **Run-through** | **匆匆撤离** | 见决定记录（2026-11-15 起） |
| Task / Quest | 任务 | 沿用现有 |
| Quest item | 任务物品 | 沿用现有 |
| Flea market | 跳蚤市场 | 沿用现有 |
| Flea offer（expired） | 挂单（已过期） | 新增 |
| Hideout stations | 藏身处工作站 | 沿用现有 |
| Air filter | 空气过滤器 | 见决定记录（2026-11-15 起，弃用旧译"过滤吸收器"）；对应藏身处"通风"（Vents）设施耗材 |
| Scav karma | Scav 声望 | 沿用现有 |
| Session（EFT session） | 会话 | 新增 |
| Profile | 档案 | 新增 |
| Account | 账户 | 新增 |
| Account ID / Profile ID | 账户 ID / 档案 ID | 新增 |
| Mode（game mode） | 模式 | 新增 |
| Nickname / Character name / Display name | 昵称 / 角色名 / 显示名称 | 新增 |
| Last played | 最近游戏 | 新增 |
| Screenshot | 截图 | 新增 |
| Media session（music） | 音乐播放会话 | 新增 |

## 3. API 令牌操作（设置页 + TarkovTracker 对话框）

| 英文 | 中文 |
|---|---|
| API token / key | API 令牌 / 令牌 |
| Import | 导入 |
| Assign | 分配 |
| Reassign | 重新分配 |
| Swap（keys） | 交换 |
| Move（key） | 移动 |
| Unbind | 解绑 |
| Remove / Delete | 移除 / 删除 |
| Unassigned / Assigned | 未分配 / 已分配 |
| Not assigned | 未分配（徽章文案） |
| Verified / Verify on assign | 已验证 / 分配时验证 |
| MANUAL ASSIGNMENT ONLY | 仅限手动分配 |
| Binding（details） | 绑定（详情） |
| Quarantined / inactive | 停用（因记录歧义或无效） |
| Scan profiles | 扫描档案 |

## 4. 界面通用词

| 英文 | 中文 | 英文 | 中文 |
|---|---|---|---|
| Cancel | 取消 | Confirm | 确认 |
| Close | 关闭 | Back | 返回 |
| Edit | 编辑 | Details | 详情 |
| Rename | 重命名 | Set name / Save name | 设置名称 / 保存名称 |
| Install / Not now | 安装 / 暂不 | Check for updates | 检查更新 |
| Copy diagnostics | 复制诊断信息 | Report（GitHub 按钮） | 反馈问题 |
| Messages | 消息 | Timers | 计时器 |
| Diagnostics | 诊断信息 | Update | 更新 |

## 5. 通知句式与占位符

带插值的文案使用编号占位符 `{0}…{n}`，中文按语序重排占位符位置，不做字符串拼接：

| 英文 | 中文 |
|---|---|
| `Raid ended on {0}.` | `在 {0} 的战局已结束` |
| `Starting a {0} raid on {1}.` | `即将在 {1} 开始 {0} 战局` |
| `{0} purchased {1} {2} for {3}` | `{0} 以 {3} 购买了 {1} 个 {2}` |
| `Matching complete on {0} after {1} seconds.` | `{0} 匹配完成，耗时 {1} 秒` |
| `Raid completed — total time in raid: {0}.` | `战局结束——本次在战局内总时长：{0}` |

复数处理：英文按计数选 key（如 `DeletedOneRaidScreenshot` / `DeletedRaidScreenshots`），中文两条值相同。双复数骨架（"Found {0} {1} and updated {2} {3}."）拆成骨架 + 单/复数词条。

## 6. 标点与风格约定

- 通知类短句**句尾不加句号**（沿用 zh 资源既有风格）；对话框正文等完整段落保留句号
- 全角标点：`，：？`；省略号用 `……`（进度/加载消息）；引号用中文弯引号 `“ ”`
- 后缀式文案（如"其档案昵称随令牌一起移动"）前导空格写在英文值内，中文值不带前导空格

## 7. 术语决定记录

| 日期 | 术语 | 决定 | 依据 |
|---|---|---|---|
| 2026-11-15 | PMC / Scav / Goons | 保留英文 | 硬核玩家社区习惯用法 |
| 2026-11-15 | Run-through | **匆匆撤离**（弃用保留英文的过渡方案） | 项目 zh 资源既有 4 条旧译均为"匆匆撤离"；大陆社区主流用词（B 站攻略视频、Reddit r/EscapefromTarkov 机制讨论）；"匆匆"准确传达"撤离成功但停留时间过短"的原意 |
| 2026-11-15 | Air filter | **空气过滤器**（弃用旧译"过滤吸收器"） | 用户实测反馈旧译不通顺；中文攻略站（TarkovBox 藏身处页、升级攻略）通行叫法为"空气过滤器"，对应藏身处"通风"设施耗材，app 的提醒功能即"进战局前开启、撤离后关闭"该过滤器 |

## 8. 两处非直译说明

- 截图删除按钮英文原文为过去式 "Deleted {0} raid screenshot(s)"（疑似笔误），中文按按钮语义译作"删除 {0} 张战局截图"
- "Shows a movable timer in Tarkov Monitor after deployment finishes" 中 "deployment" 直译为"部署"，原意如确认为"游戏加载完成后"可改为"游戏加载结束后"
