# RoomControl 网页控制台

Vue 3 + TypeScript + Vite 前端。界面按操作员任务流组织：系统总览、教室操作、配置与接入（含自动任务）、操作记录。

```powershell
npm install
npm run dev    # 开发服务器，代理 /api、/health 与 /hubs 到 http://localhost:5000
npm run build  # 类型检查并生成 dist/
```

## 界面结构

- `src/App.vue`：会话、实时连接、当前工作区、命令回执与全局状态。
- `src/components/TaskRail.vue`：按任务流排列的导航，区分值守、教室与管理。
- `src/components/OverviewWorkspace.vue`：系统总览，异常优先排序，并展示即将执行的自动任务。
- `src/components/OperateWorkspace.vue`：教室操作，定位教室、显示不可操作原因、下发指令与查看会话回执。
- `src/components/AutomationWorkspace.vue`：任务列表、执行记录与新建/修改表单；一个任务可勾选多间教室，按同名控制点批量排程，并可暂停、恢复或删除。以 `embedded` 方式嵌在配置与接入的「自动任务」步骤内，此时不再渲染自己的页头。
- `src/components/SettingsPage.vue`：按「站点 → 位置 → 教室 → 中控 → 自动任务 → 权限」的接入步骤组织配置；操作员只看到「自动任务」一步，其余步骤与接入检查仅管理员可见。
- `src/components/LogsPage.vue`：可暂停自动刷新的操作记录。
- `src/components/ui/`：状态胶囊、空状态、确认对话框、三级教室选择与图标等共享原语。

## 设计系统

样式入口为 `src/style.css`，按层引入：

1. `styles/tokens.css`：主题色板、语义状态色、间距、字号、圆角、阴影与密度令牌。
2. `styles/base.css`：重置、排版、应用外壳、任务栏与响应式布局。
3. `styles/components.css`：按钮、表单、面板、表格、胶囊、对话框、消息等组件原语。
4. `styles/pages.css`：任务流页面布局。

约束：

- 组件只使用语义令牌（如 `--success-text`、`--surface`、`--gap`），不写死颜色或魔法间距。
- 新增主题只需覆写 `[data-theme]` 中的色板；组件样式不随主题变化。
- 状态色限定为 success / warning / danger / info / neutral 五类语义。
- 显示密度（「配置与接入 · 站点与外观」）主要改变密度令牌；紧凑模式会隐藏整个工作区页头（`.workspace-header`），各工作区通过 `Teleport` 把 `.workspace-actions` 放进顶部栏槽位（`lib/layout.ts` 的 `compactModeKey` / `actionSlotKey`）。顶部栏不提供全局刷新按钮，刷新统一由各工作区自己的入口完成。偏好保存在当前浏览器。
- 总览、操作台与配置页的教室定位统一使用 `RoomCascader`（楼栋 → 楼层 → 教室）；筛选场景通过 `allow-all` 暴露「全部楼栋/楼层/教室」。
- 顶栏保留自由文本快速搜索，输入楼栋/楼层/教室或完整路径后回车跳转，适合已知目标时的快速定位。
- 左侧任务栏默认自动收起为 64px 图标栏，悬停或键盘聚焦时展开为 248px 浮层；底部固定按钮可常驻展开，偏好保存在当前浏览器。窄屏（≤900px）使用抽屉式导航。

后端启动方式、角色权限与部署说明见仓库根目录的 [README](../README.md)；REST 与 SignalR 契约见 [docs/api.md](../docs/api.md)。
