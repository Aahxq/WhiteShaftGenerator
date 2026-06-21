# 项目说明

本项目是 PromeRotation 框架内置插件。修改时保持以下约束:

- 插件入口必须实现 `IPromePlugin` 并带 `[PromePlugin]`。
- 事件来源只能使用 PR 已公开的运行时服务。
- 导出格式保持为 PR Timeline JSON。
- 不引入其他框架依赖。

