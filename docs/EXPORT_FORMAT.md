# 导出格式

每次导出会生成两份文件:

- `.json`: PR PureTimeline JSON，默认写入 PR 的 `PureTimelines/WhiteShaftGenerator` 目录继续整理。
- `.txt`: Cactbot timeline 文本，可复制到 PR 时间轴编辑器的 `从Cactbot导入` 页面。

## PR PureTimeline JSON

JSON 文件结构:

- `Meta`: 名称、作者、职业、区域和备注。
- `Anchors`: 每条记录生成一个时间轴锚点，时间使用距离战斗开始的绝对秒数。
- `Entries`: 白轴生成器不自动生成技能动作条目，默认留空，后续在 PR 编辑器中整理。

敌方读条、敌方技能效果和普通攻击会生成 PR 已支持的条件节点:

- `CastStart`
- `ActionEffect`

锚点 `Remark` 使用可读命令格式，例如 `攻击(来源:缇坦妮雅 目标:武士) +3.0s`；BOSS 读条保留技能 ID，例如 `欢快的安息日(15708)(来源:缇坦妮雅 目标:无目标) +8.1s 读条 3.7s`。

Boss 增益、队伍减益、点名、连线和其他手动开启的事件会生成普通锚点，用于保留记录上下文，后续可以在 PR PureTimeline 编辑器里手动整理。

## Cactbot timeline 文本

TXT 文件使用 PR Cactbot 导入器支持的时间轴语法，时间字段使用距离战斗开始的绝对秒数:

- 文件头包含副本名、`ZoneId` 和 `hideall "--sync--"`。
- 战斗开始写为 `0.0 "--sync--" InCombat { inGameCombat: "1" } window 0,1`。
- BOSS 读条写为 `StartsUsing { id: "技能ID", source: "来源名" } duration 秒数 window ...`。
- 敌方技能效果和普通攻击写为 `Ability { id: "技能ID", source: "来源名" } window ...`。
- Boss 增益、队伍减益、点名、连线、地图/对象、场地标记等没有直接同步条件的事件写为普通锚点。
- 对象可选中/不可选中会输出为 `--targetable--` / `--untargetable--` 普通锚点。

导入后，读条和技能效果会映射为 PR 已支持的同步条件；普通锚点保留时间和名称用于后续手动整理。

