# RoomControl

RoomControl 是面向单站点内网部署的教室/会议室集中控制平台。首期包含教室与 CIP 控制点管理、状态快照、网页指令、实时状态推送、自动任务与手动任务；真实中控硬件及摄像头功能暂不在首期范围内。

网页分为**控制台**与**配置**两部分：控制台用于查看数字状态、发送脉冲并触发展示在系统总览上的手动任务；配置页按接入顺序拆分为「站点设置」「楼栋与楼层」「教室管理」「中控与设备」「自动任务」「手动任务」「用户与权限」，另提供对三种角色开放的「外观与显示」，可从左侧「配置与接入」进入后按步骤切换。其中「外观与显示」维护主题与显示密度，管理员、操作员和观看员均可调整；品牌标识（左上角 Logo 与名称）、教室（含楼栋与楼层）、中控连接、数字控制点映射、定时与手动任务在对应的配置子页面维护；用户管理用于创建账号并分配管理员/操作员/观看员角色。

## 架构

- **API / 后台服务**：.NET 10、ASP.NET Core、EF Core、Quartz.NET、SignalR。
- **网页控制台**：Vue 3、TypeScript、Vite。
- **状态链路**：CIP 中控 → 后端适配器 → 后端状态存储 → REST 快照 / SignalR 增量推送 → 网页。
- **状态持久化**：数据库保存 Join 最新快照、最后上报时间、自动任务和执行记录；在线连接保存在后端进程内。中控断开后状态会标记离线，界面保留最近一次状态及时间。API 完全启动（开始监听并完成数据库初始化）后才建立中控连接，避免启动阶段的连接失败记录干扰排查。
- **数据库**：SQLite，适合单站点内网部署，无需单独安装数据库服务。添加其他品牌时实现独立控制器适配器，不改动教室/API/前端模型。
- **CIP 首期**：TCP 客户端仅支持 CIP Raw，对接现有 CIP 模拟器。协议实现以模拟器代码为互操作性依据，不代表通过真实设备认证。
- **协议与接口文档**：中控协议说明集中维护在 [docs/controller-protocols/](./docs/controller-protocols/)，新增协议时请同时登记索引并添加对应 Markdown 文件；REST 与 SignalR 契约、字段约束和错误码见 [docs/api.md](./docs/api.md)；数据库表结构、索引与删除行为见 [docs/database.md](./docs/database.md)。

## 本机开发（Windows）

需要 .NET 10 SDK 和 Node.js 20.19+。

### 使用 VS Code 运行配置

1. 将仓库根目录的 `.env.example` 复制为 `.env`，并将管理员密码替换为至少 12 个字符的本地强密码。`.env` 已加入忽略规则，不会提交到仓库。
2. 安装 VS Code 的 C# Dev Kit 扩展，并在仓库根目录打开工作区。
3. 打开“运行和调试”，选择 **RoomControl Full Stack** 启动前后端；首次运行前端需先执行一次 `npm install`（在 `web` 目录）。

也可单独启动 **RoomControl API** 或 **RoomControl Web**。API 使用 Development 配置和 SQLite，监听 `http://localhost:5000`；前端 Vite 使用本机地址并代理 API 与 SignalR 请求。需要从其他电脑访问调试页面时，改用下方「让其他电脑访问开发环境」中的 **(LAN)** 配置。

### 命令行运行

1. 在 PowerShell 启动后端：

   ```powershell
   $env:Authentication__AdminUser = "admin"
   $env:Authentication__AdminPassword = "请替换为至少12位的本地密码"
   Set-Location backend\RoomControl.Api
   dotnet run --urls http://localhost:5000
   ```

   Development 环境默认使用 `backend\RoomControl.Api\roomcontrol.db`。数据库首次启动时由 EF Core 创建。

2. 另开 PowerShell 启动网页：

   ```powershell
   Set-Location web
   npm install
   npm run dev
   ```

   打开 Vite 输出的本机地址。开发服务器只监听本机，`/api` 与 `/hubs` 代理到 `http://localhost:5000`。

3. 登录后进入**配置**页：先添加楼栋与楼层，再添加教室（楼栋、楼层只能从已配置项中选择），随后选择中控类型并填写 CIP 模拟器地址、端口与 IPID，最后添加数字控制点并分别设置发送、状态接收和使能接收 Join；左侧教室列表按楼栋、楼层分组显示，同一时间最多展开一个楼栋，且该楼栋内最多展开一个楼层。使能 Join 默认为 `0`，表示不设置并默认允许操作；配置有效 Join 编号后需上报高电平才允许操作。每个控制点可单独设置「操作前确认」：开启后控制台点击该控制点会先弹出确认弹窗再发送指令，删除该控制点时同样先确认；关闭后两者都直接执行（新增控制点默认开启，旧数据升级后同样默认开启）。控制点列表可拖动左侧手柄上下排序，控制台按钮按保存后的顺序排列，新增控制点默认排在末尾。首期仅使用 CIP Raw，模拟器的 IP 表须包含该 IPID；默认端口为 `41794`。

已有模拟器项目：`D:\zld\visual studio\Projects\CIP-Server`。启动模拟器并配置 IP 表后，再在 RoomControl 中添加中控连接。

### 让其他电脑访问开发环境（远程调试）

需要在另一台电脑上打开页面、断点仍在本机 VS Code 中生效时：

1. 在管理员 PowerShell 中放行开发端口（只对 Domain/Private 配置和本子网生效）：

   ```powershell
   New-NetFirewallRule -DisplayName "RoomControl Dev Web 5173" -Direction Inbound `
     -Protocol TCP -LocalPort 5173 -Action Allow -Profile Domain,Private -RemoteAddress LocalSubnet
   New-NetFirewallRule -DisplayName "RoomControl Dev API 5000" -Direction Inbound `
     -Protocol TCP -LocalPort 5000 -Action Allow -Profile Domain,Private -RemoteAddress LocalSubnet
   ```

2. 在“运行和调试”中选择 **RoomControl Full Stack (LAN)**：后端改为监听 `http://0.0.0.0:5000`，前端以 `npm run dev:host` 监听 `0.0.0.0:5173`。
3. 查看 Vite 输出的 `Network:` 地址，在另一台电脑打开 `http://<开发机IP>:5173`。页面请求由 Vite 代理转发到本机 `http://localhost:5000`，代理已开启 `xfwd`，登录限流和操作日志仍按真实客户端 IP 统计。
4. 只想查看页面、不需要联调后端时，也可在 `web` 目录执行 `npm run dev:host`，后端使用已发布程序。

仅限同一可信内网使用；`.env` 中的管理员密码与数据库路径不变，不要把开发端口暴露到公网。

## Windows 一键部署（SQLite）

需要 Windows、.NET 10 SDK 和 Node.js 20.19+（在部署机上直接构建）。如果目标机不方便安装开发工具，请改用下方的[打包发布（离线部署包）](#打包发布离线部署包)。以管理员身份打开 PowerShell，在项目根目录运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

按提示设置管理员用户名和至少 12 位密码。脚本将构建前后端、把网页部署到 API、在本机初始化 SQLite 数据库并启动服务；完成后会自动打开浏览器。默认监听 `http://0.0.0.0:5000`，可用参数指定其他监听地址：

```powershell
.\install.ps1 -ListenUrl "http://0.0.0.0:5000"
```

默认安装目录为 `C:\Program Files\RoomControl`，SQLite 数据库与日志位于 `C:\ProgramData\RoomControl`。升级时再次运行脚本会保留数据库；脚本输入的初始管理员密码只在数据库首次创建管理员时生效。可使用安装目录中的 `Start-RoomControl.ps1` 和 `Stop-RoomControl.ps1` 手动启动或停止服务。

如需从其他电脑访问，请配置 HTTPS 反向代理并转发 `X-Forwarded-For`、`X-Forwarded-Proto`；不要将 HTTP 服务直接暴露到公网。定期备份 `C:\ProgramData\RoomControl\roomcontrol.db`。目前一键脚本会启动应用进程，但不会注册 Windows 服务或自动配置防火墙。

`EnsureCreated` 仅用于首期原型初始化；生产环境升级前应补齐并应用 EF Core migrations、HTTPS 反向代理、数据保护密钥持久化、备份与访问策略。

启动时会自动补齐已有数据库缺失的增量结构（教室的楼栋/楼层列、`SiteSettings` 及其日志保留天数、`Buildings`/`Floors` 表），因此从初始化版本升级无需手工迁移；若数据库中已有带楼栋、楼层的教室且尚未配置楼栋，会按教室数据自动生成对应的楼栋与楼层。

## 打包发布（离线部署包）

打包机需要 Windows、.NET 10 SDK 和 Node.js 20.19+。在项目根目录运行：

```powershell
.\publish.ps1
```

脚本会构建前端、以框架依赖方式发布后端（默认 `win-x64`）、把网页产物放入 `wwwroot`，并输出：

- `artifacts\RoomControl-<版本>-win-x64\`：可直接复制的发布目录
- `artifacts\RoomControl-<版本>-win-x64.zip`：发布压缩包，脚本同时打印 SHA256

版本号默认取 `git describe --tags --always`，可用 `-Version` 指定，例如 `.\publish.ps1 -Version 1.0.0`。本机正在运行 Vite 开发服务器时会占用 `node_modules` 中的 esbuild 导致 `npm ci` 失败，此时加 `-SkipNpmInstall` 跳过依赖还原、直接用现有依赖构建。

发布包内含 `Install-RoomControl.ps1`，目标机只需预装 **.NET 10 ASP.NET Core 运行时**，不需要 .NET SDK 或 Node.js。将压缩包解压到任意目录，以管理员身份运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-RoomControl.ps1
```

安装行为与源码部署一致：默认安装到 `C:\Program Files\RoomControl`，SQLite 数据库与日志位于 `C:\ProgramData\RoomControl`，生成 `Start-RoomControl.ps1`/`Stop-RoomControl.ps1`，启动服务并打开浏览器；同样支持 `-InstallDir`、`-DataDir`、`-ListenUrl` 参数。升级时重新解压并再次运行安装脚本即可，数据库保留。

## 功能与 API

REST 接口位于 `/api`，实时推送使用 SignalR（`/hubs/control`），健康检查为 `/health`。所有接口共用同源 HttpOnly Cookie 会话，JSON 属性名为 camelCase、枚举序列化为字符串，错误统一返回 ProblemDetails。完整的请求/响应示例、字段约束、权限矩阵、限流规则、SignalR 事件与业务规则见 **[docs/api.md](./docs/api.md)**；数据库表结构、约束与删除行为见 [docs/database.md](./docs/database.md)。

| 分类 | 端点 | 权限 |
| --- | --- | --- |
| 健康检查 | `GET /health` | 匿名 |
| 站点品牌 | `GET /api/branding`、`PUT /api/branding` | 读取免登录 / 保存需管理员 |
| 登录与会话 | `GET /api/auth/accounts`、`POST /api/auth/login`、`POST /api/auth/logout`、`GET /api/auth/me`、`POST /api/auth/change-password` | 前两项匿名并限流，其余需登录 |
| 用户管理 | `GET/POST /api/users`、`PUT/DELETE /api/users/{id}` | 管理员 |
| 楼栋与楼层 | `GET /api/buildings`；`POST /api/buildings`、`PUT/DELETE /api/buildings/{buildingId}`、`POST /api/buildings/{buildingId}/floors`、`PUT/DELETE /api/floors/{floorId}` | 读取任意登录用户 / 写操作需管理员 |
| 教室 | `GET/POST /api/rooms`、`PUT/DELETE /api/rooms/{roomId}` | 读取任意登录用户 / 写操作需管理员 |
| 中控类型 | `GET /api/controller-types` | 任意登录用户 |
| 中控连接 | `POST /api/rooms/{roomId}/controller`、`PUT/DELETE /api/controllers/{controllerId}` | 管理员 |
| 数字控制点 | `POST /api/controllers/{controllerId}/joins`、`PUT/DELETE /api/joins/{mappingId}`、`PUT /api/controllers/{controllerId}/joins/order`、`POST /api/joins/{mappingId}/copy`、`POST /api/controllers/{controllerId}/joins/copy-all` | 管理员 |
| 控制指令 | `POST /api/joins/{mappingId}/pulse`（返回 `202`） | 操作员或管理员 |
| 自动任务 | `GET /api/schedules`、`GET /api/schedules/{scheduleId}/executions`；`POST /api/schedules`、`PUT/DELETE /api/schedules/groups/{groupId}`、`POST /api/schedules/groups/{groupId}/pause`、`POST /api/schedules/groups/{groupId}/resume`、`DELETE /api/schedules/{scheduleId}` | 读取任意登录用户 / 写操作需操作员或管理员 |
| 手动任务 | `GET /api/manual-tasks`、`GET /api/manual-tasks/{taskId}/executions`；`POST /api/manual-tasks`、`PUT/DELETE /api/manual-tasks/{taskId}`、`POST /api/manual-tasks/{taskId}/run` | 读取任意登录用户 / 写操作与执行需操作员或管理员 |
| 操作日志 | `GET /api/logs`（按 `category`、`from`/`to` 筛选，不设固定条数上限）、`GET/PUT /api/logs/settings`（自动清理保留天数） | 查看任意登录用户；保留设置仅管理员 |
| 实时推送 | `/hubs/control`，客户端调用 `JoinRoom(roomId)` / `LeaveRoom(roomId)`；接收 `joinStateChanged`、`controllerStatusChanged` | 任意登录用户 |

界面相关的关键约定：

- 控制点创建与更新可带 `showConfirmationPopup`（默认 `true`），决定控制台发送指令与删除控制点前是否弹窗确认，`GET /api/rooms` 会返回该开关。按 `PUT /api/controllers/{controllerId}/joins/order` 提交的完整顺序渲染控制台按钮；单点复制会替换目标上相同发送 Join 的既有控制点，整组复制会先清空目标控制点再按源顺序写入。
- 自动任务的 `joinMappingIds` 可一次覆盖多间教室的同名控制点，同一任务的目标共享 `groupId`；修改整组会保留仍在列表中的目标、删除被移除目标及其执行记录。任务按服务器本地时间执行，支持一次性与按周重复，可暂停与恢复。
- 手动任务把多间教室的同名控制点聚合为一个系统总览按钮；点击后逐个目标教室下发一次脉冲，单间失败不影响其余教室，结果按次保存为执行摘要。任务可配置执行前二次确认，且与自动任务一样不受使能 Join 限制。总览按钮的排列顺序可在「配置与接入 · 手动任务」拖动任务名称调整，顺序保存在服务端。
- 中控上报的状态变化通过 `joinStateChanged` 增量推送，连接上下线通过 `controllerStatusChanged` 推送；`GET /api/rooms` 返回当前快照，刷新页面后应重新拉取。
- 操作日志按 `authentication`、`configuration`、`control`、`schedule`、`state`、`connection` 分类，记录可读操作对象，不保存请求正文或密码；认证类记录的对象栏为来源 IP。

### 权限与会话安全

- 角色分为 `admin`（管理员）、`operator`（操作员）、`viewer`（观看员）。用户管理、站点配置、楼栋/楼层、教室、中控与控制点维护要求 `admin`；发送脉冲、创建、修改、暂停、恢复或删除自动任务、创建或执行手动任务要求 `operator` 或 `admin`；其余查询接口允许任何已登录用户。系统总览中的「去接入中控」仅对 `admin` 显示，点击后进入「配置与接入 · 中控与设备」并自动选中该教室；其他角色看到的是一句联系管理员提示。
- `viewer` 登录后仅显示只读的「系统总览」：教室卡片显示楼栋/楼层、中控在线状态、中控连接信息与每个控制点的状态；控制点只有高/低两种状态，尚未收到上报的按低电平（关闭）显示。观看员不显示「需要处理」「即将自动执行」两栏及待处理计数，也不显示教室操作与配置入口，不能点击教室卡片进入操作台。
- 自动任务属于「配置与接入」的第 5 步，手动任务为第 6 步：`operator` 可以进入配置与接入，但只能看到「自动任务」「手动任务」与「外观与显示」，其余接入步骤与接入检查仅 `admin` 可见；没有配置权限的账号进入配置与接入时会自动落到自动任务步骤。
- 操作记录页和日志 API 对任意已登录角色开放（管理员、操作员、观看员均可查看）；自动清理保留天数仍仅管理员可修改。日志按认证、配置、控制、状态、连接和任务分类（任务栏包含自动任务与手动任务的改动记录），保留可读操作（如「发送 Join 5」「删除控制点 Join 1」）、操作对象（控制点等对象会带上所属教室，如「教室「A楼 · 1F · R101」· 控制点「上课」」）、操作者、HTTP 结果与 UTC 时间。对象名称在写入时解析，删除操作的日志同样能显示被删除对象及其教室。设备上报引起的控制点状态变化使用「状态」分类记录（如「状态变更 Join 6：低电平 → 高电平」，操作者为「设备」），仅在电平真正变化时写入，避免周期回报刷满日志。自动任务到点自动执行时，每条下发指令都会写入一条「任务」分类的操作记录（动作如「自动执行自动任务 Join 7」，操作者为「系统」，对象包含教室、控制点与任务名称，结果码反映该条指令成败），逐教室结果同时保留在执行记录中；停机期间错过的一次性任务不产生发送指令，仍按组记为「自动任务未自动执行」。手动任务每次点击除一条汇总记录外，也会为每个目标教室的下发指令各写一条「控制」分类记录；手动任务的创建、修改与删除同样记入「任务」分类。操作记录页每 5 秒自动刷新，可在页面内暂停，页面隐藏时也会暂停，切回后立即刷新。日志查看支持按日期区间筛选，进入页面默认只读取近 3 天（含今天）的记录，可清除或调整区间查看更早记录，不再限制 500 条；可在「配置与接入 · 站点设置」设置自动清理的保留天数（默认 60 天，`0` 表示永久保留），保存后立即清理，之后每天凌晨 3 点自动执行。认证类记录会把来源 IP 写入对象栏（经 `X-Forwarded-For` 解析真实客户端地址），登录成功时操作者为登录账号，登录失败时保持 `anonymous`。
- 登录页账号下拉框接口（`GET /api/auth/accounts`）按来源 IP 限流：每分钟最多 60 次；只返回启用账号的用户名与角色。
- 登录接口按来源 IP 限流：每分钟最多 20 次尝试，超出返回 `429`。同一账号连续失败 5 次后锁定 5 分钟，锁定期间统一返回 `401`（与凭据错误一致，不暴露账号状态）。
- 反向代理终止 TLS 时应转发 `X-Forwarded-For` 与 `X-Forwarded-Proto`（默认仅信任本机代理，远程代理需配置 `KnownProxies`）：限流按真实客户端 IP 统计，HTTPS 下会话 Cookie 带 `Secure`。
- 日志中的来源 IP 优先取经 `X-Forwarded-For` 解析的地址；若代理只写 `X-Real-IP` 或 `CF-Connecting-IP`，且连接来自本机代理，也会采信这些头。直接本机访问时回环地址统一显示为 `IP 127.0.0.1`。开发用 Vite 代理已开启 `xfwd` 转发客户端 IP。
- 登录使用 HttpOnly + `SameSite=Strict` Cookie，8 小时滑动过期。每次请求都会校验账号是否仍然存在且启用：账号被禁用或删除后会话立即失效，管理员调整角色后新权限立即生效。
- 密码使用 PBKDF2-SHA256（210,000 次迭代，每账号独立随机盐）存储，长度限制为 6–128 个字符；用户名与密码均使用固定时间比较，避免通过响应差异判断账号是否存在，超长输入在 KDF 之前直接拒绝。

### 界面组织与设计系统

- 界面按操作员任务流组织，而不是按页面堆叠：**值守 → 教室操作 → 配置与接入（含自动任务与手动任务）→ 操作记录**。系统总览先列出离线、未接入与逾期任务，并提供手动任务按钮；教室操作按「定位 → 确认中控/使能状态 → 下发 → 核对回执」推进。配置与接入按接入顺序分为站点设置、楼栋与楼层、教室、中控与设备、自动任务、手动任务、用户与权限七步，另提供对三种角色开放的「外观与显示」步骤。
- 教室操作的设备控制区左侧预留视频窗口（固定 16:9 的虚线占位，接入教室摄像头后替换为实时画面），控制点按钮按配置顺序自上而下排列在视频窗口右侧，与视频同高，超出时在列内滚动；窄屏下改为视频在上、按钮在下。
- 显式状态：控制点卡片只保留名称与二次确认盾牌，开/关由边框与底色区分，图例给出对照；控制点只有高/低两种状态，尚未收到回报的按低电平（关闭）显示，不出现「未知」。无法下发的控制点（中控离线、连接停用、使能信号未就绪、只读账号）直接置灰并禁用点击，具体原因保留在悬停提示中。卡片底栏显示该控制点最近的启用任务：倒计时与任务名称，没有待执行任务时显示「无自动任务」。下发结果集中在「操作回执」面板：默认收起，点击页头（紧凑模式下为顶部栏）的「操作回执」按钮展开，按钮始终显示本次会话的回执条数，出现失败时标记为红色，展开后可用面板右上角 × 收起；发送中的卡片会切换为进行中样式，每次下发也会写入服务端操作记录。
- 视觉密度：在「配置与接入 · 外观与显示」切换「标准 / 紧凑」。紧凑模式压缩行高、内边距与正文字号，并隐藏整个工作区页头，把该页的操作（刷新、快照时间、自动刷新开关、接入检查结论）传送到顶部栏；顶部栏不单独放全局刷新按钮。选择保存在当前浏览器。
- 左侧任务栏默认自动收起为图标栏，悬停或键盘聚焦时展开为浮层；底部「固定展开」可保持常驻，偏好按浏览器保存。窄屏下改为抽屉式导航。
- 设计系统：`web/src/styles/tokens.css` 定义主题与语义色、间距、字号、密度；`base.css`、`components.css`、`pages.css` 分层组合。组件只引用语义令牌，不写死十六进制颜色，主题只覆写色板。

自动任务支持一次性和按周重复，统一按服务器本地时间执行。一个任务可同时作用于多间教室：勾选教室后按同名控制点对齐，每间教室各自生成一个执行目标（共享 `groupId`），执行记录按教室分别保留；任务可随时修改名称、时间、重复日与教室范围，移除的教室连同其执行记录一并删除。任务可暂停与恢复：暂停后保留定义与执行记录但不再触发，恢复时按当前时间重算下一次执行，一次性任务的时间已过则需先修改执行时间。任务列表按计划时间排列（一次性任务按执行时刻在前，每周任务按本地执行时间），与启用状态和下次执行时间无关，暂停或恢复后任务不会换位。删除任务会同时清除目标与执行记录。目标中控离线或任务错过一次性执行时间时记录失败，不自动补发；每次自动执行都会为每条下发指令写入一条操作记录（包含教室、控制点、任务名与发送 Join），便于事后按教室追溯。使能 Join 只限制人工操作，不影响自动任务。指令已写入 TCP 连接不代表设备状态已改变；网页状态以中控回报的 Join 值为准。

手动任务同样按同名控制点覆盖多间教室，但不按时间调度：每个任务在系统总览显示为一个按钮，点击后立即向全部目标教室逐个下发脉冲，并按次数保存成功/失败数量与失败原因摘要，每个目标教室的下发指令也会各写入一条操作记录。任务可配置执行前二次确认；与自动任务一致，手动任务不受使能 Join 限制，只要求中控在线。单间失败不会中断其余教室，执行记录在「配置与接入 · 手动任务」中查看。

删除 Join、中控或教室时，关联自动任务、手动任务及其执行记录会一并清理，不会留下指向已删除控制点的任务。

## 验证

```powershell
dotnet test backend\RoomControl.Api.Tests\RoomControl.Api.Tests.csproj
Set-Location web
npm run build
```
## 说明
代码由AI辅助编写