# RoomControl API 参考

本文档描述 RoomControl 后端对外提供的 HTTP API 与 SignalR 实时通道。端点实现位于 [Program.cs](../backend/RoomControl.Api/Program.cs)（Minimal API）、[ControlHub.cs](../backend/RoomControl.Api/Hubs/ControlHub.cs)（实时推送），请求模型位于 [ApiContracts.cs](../backend/RoomControl.Api/Contracts/ApiContracts.cs)。

## 目录

- [1. 基础约定](#1-基础约定)
- [2. 认证、角色与权限](#2-认证角色与权限)
- [3. 错误、限流与审计](#3-错误限流与审计)
- [4. 端点总览](#4-端点总览)
- [5. 运行状态与站点品牌](#5-运行状态与站点品牌)
- [6. 认证与会话](#6-认证与会话)
- [7. 用户管理](#7-用户管理)
- [8. 楼栋与楼层](#8-楼栋与楼层)
- [9. 教室](#9-教室)
- [10. 中控连接](#10-中控连接)
- [11. 数字控制点](#11-数字控制点)
- [12. 控制指令](#12-控制指令)
- [13. 定时任务](#13-定时任务)
- [14. 操作日志](#14-操作日志)
- [15. 实时推送（SignalR）](#15-实时推送signalr)
- [16. 数据模型](#16-数据模型)
- [17. 业务规则与生命周期](#17-业务规则与生命周期)

## 1. 基础约定

| 项目 | 说明 |
| --- | --- |
| 传输 | HTTP/1.1，默认同源部署；生产环境应由反向代理终止 TLS |
| 前缀 | 业务接口统一为 `/api/*`；健康检查为 `/health`；实时通道为 `/hubs/control` |
| 请求格式 | `Content-Type: application/json`，UTF-8 |
| 响应格式 | JSON，属性名使用 **camelCase**，枚举序列化为字符串（如 `"Once"`、`"Weekly"`） |
| 标识 | 所有资源 ID 为 GUID 字符串 |
| 时间 | 带 `*Utc` 后缀的字段为 UTC 时间点，ISO 8601 格式（如 `2026-01-01T08:00:00+00:00`）；`runAtLocal` 为服务器本地墙上时间，`localTime` 为 `HH:mm:ss` |
| 空响应 | 更新、删除成功返回 `204 No Content`，无响应体 |
| 静态资源 | 非 `/api`、`/health`、`/hubs` 的 GET 请求回落到 `wwwroot/index.html`（单页应用路由） |

**时间语义**：平台按单一站点部署，所有定时任务使用**服务器本地时间**解释和执行；接口中的 `*Utc` 字段只用于跨端传输与展示。修改服务器时区会改变已有每周任务的触发时刻。

**并发与实时性**：控制点状态以中控上报为准。REST 快照接口返回数据库中的最新快照，SignalR 推送增量变化；两者配合使用，不能把“指令已写入 TCP”视为设备状态已变更。

## 2. 认证、角色与权限

### 2.1 会话

- 登录成功后服务端下发 Cookie `RoomControl.Auth`：`HttpOnly`、`SameSite=Strict`、滑动过期 8 小时；HTTPS 请求下附带 `Secure`。
- 除标注 **匿名** 的端点外，所有 `/api` 端点与 `/hubs/control` 都要求有效会话。
- 每次请求都会重新校验账号是否仍然存在且启用：账号被禁用或删除后会话立即失效（返回 `401`），角色被调整后新权限立即生效。
- 未认证返回 `401`，已认证但角色不足返回 `403`，两者均为*空响应体*（Cookie 认证不重定向到登录页）。

### 2.2 角色

| 角色标识 | 显示名 | 权限范围 |
| --- | --- | --- |
| `admin` | 管理员 | 全部配置、用户管理、操作日志、控制指令、定时任务 |
| `operator` | 操作员 | 查看状态、发送控制指令、创建/修改/暂停/恢复/删除定时任务 |
| `viewer` | 观看员 | 只读：查看教室、控制点状态与中控在线情况 |

### 2.3 权限策略

接口使用两种策略：

- `AdminOnly`：仅 `admin`。
- `OperatorOrAdmin`：`admin` 或 `operator`。
- 未标注策略的业务端点：任意已登录角色（含 `viewer`）。

## 3. 错误、限流与审计

### 3.1 错误响应

未捕获的异常由全局异常处理器转为 [RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807)：

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Invalid request",
  "status": 400,
  "detail": "Room name is required and must be at most 120 characters."
}
```

| 异常类型 | HTTP 状态 | `title` |
| --- | --- | --- |
| `KeyNotFoundException` | `404` | `Not found` |
| `ArgumentException` | `400` | `Invalid request` |
| `ControllerUnavailableException` | `503` | `Controller unavailable` |
| `InvalidOperationException` | `409` | `Operation cannot be completed` |
| 其他异常 | `500` | `Unexpected server error`（`detail` 不返回，错误写入服务端日志） |

少数端点返回自定义的 `400` 响应体 `{ "detail": "…" }`（用户创建/删除、修改密码），其余端点统一使用 ProblemDetails。

### 3.2 限流

| 端点 | 维度 | 限制 | 超出时 |
| --- | --- | --- | --- |
| `POST /api/auth/login` | 来源 IP | 每分钟 20 次 | `429` |
| `GET /api/auth/accounts` | 来源 IP | 每分钟 60 次 | `429` |

另有账号级登录保护：同一账号在 15 分钟内连续失败 5 次后锁定 5 分钟；锁定期间与凭据错误一样返回 `401`，不暴露账号状态。登录成功即清除失败计数。

来源 IP 通过 `X-Forwarded-For` 等可信代理头解析（仅信任本机代理；远程代理需配置 `KnownProxies`）。

### 3.3 审计

中间件记录所有 `/api` 下的 `POST`/`PUT`/`PATCH`/`DELETE` 请求（`GET` 不记录）：

- 不保存请求正文、密码或 Cookie，只记录分类、可读操作名、操作对象、操作者、HTTP 状态码与 UTC 时间。
- 操作对象在请求执行前解析，因此删除操作仍能记录被删除对象及其所属教室。
- 创建类请求优先使用响应 `Location` 头解析新对象。
- 设备上报引起的电平变化与中控上下线由后台写入，操作者分别为「设备」和「系统」。
- 审计写入失败只记录服务端告警，不影响原请求结果。
- 系统每天凌晨 3 点（服务器本地时间）按「日志保留天数」清理过期记录，启动时也会清理一次；默认保留 60 天，设为 `0` 表示永久保留。

## 4. 端点总览

| 方法 | 路径 | 权限 | 说明 |
| --- | --- | --- | --- |
| GET | `/health` | 匿名 | 数据库连通性检查 |
| GET | `/api/branding` | 匿名 | 读取站点名称、副标题与 Logo |
| PUT | `/api/branding` | admin | 保存站点品牌 |
| GET | `/api/auth/accounts` | 匿名（限流 60/分钟/IP） | 登录页账号列表 |
| POST | `/api/auth/login` | 匿名（限流 20/分钟/IP） | 登录并建立会话 |
| POST | `/api/auth/logout` | 已登录 | 退出登录 |
| GET | `/api/auth/me` | 已登录 | 当前账号与角色 |
| POST | `/api/auth/change-password` | 已登录 | 修改自己的密码 |
| GET | `/api/users` | admin | 账号列表 |
| POST | `/api/users` | admin | 创建账号 |
| PUT | `/api/users/{id}` | admin | 更新账号角色、启用状态或密码 |
| DELETE | `/api/users/{id}` | admin | 删除账号 |
| GET | `/api/buildings` | 已登录 | 楼栋与楼层树 |
| POST | `/api/buildings` | admin | 创建楼栋 |
| PUT | `/api/buildings/{buildingId}` | admin | 重命名楼栋 |
| DELETE | `/api/buildings/{buildingId}` | admin | 删除楼栋 |
| POST | `/api/buildings/{buildingId}/floors` | admin | 在楼栋下创建楼层 |
| PUT | `/api/floors/{floorId}` | admin | 重命名楼层 |
| DELETE | `/api/floors/{floorId}` | admin | 删除楼层 |
| GET | `/api/controller-types` | 已登录 | 支持的中控类型 |
| GET | `/api/rooms` | 已登录 | 教室、中控与控制点状态快照 |
| POST | `/api/rooms` | admin | 创建教室 |
| PUT | `/api/rooms/{roomId}` | admin | 更新教室名称与位置 |
| DELETE | `/api/rooms/{roomId}` | admin | 删除教室及其中控 |
| POST | `/api/rooms/{roomId}/controller` | admin | 为教室接入中控 |
| PUT | `/api/controllers/{controllerId}` | admin | 更新中控连接配置 |
| DELETE | `/api/controllers/{controllerId}` | admin | 移除中控 |
| POST | `/api/controllers/{controllerId}/joins` | admin | 创建数字控制点 |
| PUT | `/api/controllers/{controllerId}/joins/order` | admin | 保存控制点顺序 |
| POST | `/api/controllers/{controllerId}/joins/copy-all` | admin | 复制全部控制点到其他教室 |
| PUT | `/api/joins/{mappingId}` | admin | 更新控制点 |
| DELETE | `/api/joins/{mappingId}` | admin | 删除控制点 |
| POST | `/api/joins/{mappingId}/copy` | admin | 复制单个控制点到其他教室 |
| POST | `/api/joins/{mappingId}/pulse` | operator / admin | 发送一次脉冲 |
| GET | `/api/schedules` | 已登录 | 全部定时任务目标 |
| POST | `/api/schedules` | operator / admin | 创建定时任务组 |
| PUT | `/api/schedules/groups/{groupId}` | operator / admin | 修改整组任务 |
| DELETE | `/api/schedules/groups/{groupId}` | operator / admin | 删除整组任务 |
| POST | `/api/schedules/groups/{groupId}/pause` | operator / admin | 暂停整组任务 |
| POST | `/api/schedules/groups/{groupId}/resume` | operator / admin | 恢复整组任务 |
| DELETE | `/api/schedules/{scheduleId}` | operator / admin | 删除单个任务目标 |
| GET | `/api/schedules/{scheduleId}/executions` | 已登录 | 单个任务目标的执行记录 |
| GET | `/api/logs` | admin | 操作日志 |
| GET | `/api/logs/settings` | admin | 读取日志保留天数 |
| PUT | `/api/logs/settings` | admin | 更新日志保留天数并立即清理 |
| WebSocket | `/hubs/control` | 已登录 | SignalR 实时状态推送 |

## 5. 运行状态与站点品牌

### `GET /health`

匿名。检查数据库连通性，不返回版本或敏感信息。

```json
{ "status": "ok", "database": "ok" }
```

数据库不可用时返回 `503` ProblemDetails。

### `GET /api/branding`

匿名。登录页在认证前读取品牌信息。

```json
{
  "displayName": "RoomControl",
  "subtitle": "教室中控平台",
  "logoDataUrl": null
}
```

### `PUT /api/branding`

仅 `admin`。保存站点名称、副标题与 Logo。

请求：

```json
{
  "displayName": "一号教学楼中控",
  "subtitle": "教室集中控制平台",
  "logoDataUrl": "data:image/png;base64,iVBORw0KGgo..."
}
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `displayName` | string | 必填，≤ 60 字符 |
| `subtitle` | string | ≤ 80 字符，可为空 |
| `logoDataUrl` | string \| null | 必须为 `data:image/` 开头的 Data URL，≤ 300,000 字符（约 200 KB 图片），传 `null` 清除 |

响应：`200`，返回保存后的 `{ displayName, subtitle, logoDataUrl }`。

## 6. 认证与会话

### `GET /api/auth/accounts`

匿名，按 IP 限流（60 次/分钟）。供登录页账号下拉框使用，仅返回启用账号：

```json
[
  { "username": "admin", "role": "admin" },
  { "username": "operator1", "role": "operator" }
]
```

### `POST /api/auth/login`

匿名，按 IP 限流（20 次/分钟）。

请求：

```json
{ "username": "admin", "password": "••••••••" }
```

响应：

| 状态 | 说明 |
| --- | --- |
| `204` | 登录成功，响应附带会话 Cookie |
| `401` | 用户名/密码错误，或账号锁定/已禁用（不区分原因，无响应体） |
| `429` | 超过 IP 限流 |

### `POST /api/auth/logout`

需要登录。注销当前会话，返回 `204`。

### `GET /api/auth/me`

需要登录。返回当前会话身份：

```json
{ "username": "admin", "role": "admin" }
```

### `POST /api/auth/change-password`

需要登录。修改当前账号密码。

请求：

```json
{ "oldPassword": "旧密码", "newPassword": "新密码" }
```

| 状态 | 说明 |
| --- | --- |
| `204` | 修改成功 |
| `400` | 原密码不正确、账号已禁用（`{ "detail": "…" }`），或新密码少于 6 个字符（ProblemDetails） |

## 7. 用户管理

以下端点全部要求 `admin`。

### `GET /api/users`

按用户名升序返回账号，不含密码哈希或盐：

```json
[
  {
    "id": "8c0f7a2e-2d43-4c8b-9a5e-1f2b3c4d5e6f",
    "username": "admin",
    "role": "admin",
    "roleDisplayName": "管理员",
    "isActive": true,
    "createdAt": "2026-01-01T08:00:00+00:00"
  }
]
```

### `POST /api/users`

请求：

```json
{
  "username": "operator1",
  "password": "至少6位密码",
  "role": "operator",
  "isActive": true
}
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `username` | string | 必填，≤ 60 字符，去首尾空白，不能包含控制字符，全局唯一 |
| `password` | string | 必填，≥ 6 字符 |
| `role` | string | `admin` / `operator` / `viewer` |
| `isActive` | bool | 是否启用 |

响应：`201 Created`，`Location: /api/users/{id}`，响应体为 `UserDto`。用户名重复、角色无效等情况返回 `400`。

### `PUT /api/users/{id}`

部分更新，`null` 字段表示不修改：

```json
{ "password": null, "role": "viewer", "isActive": true }
```

限制：

- 不能修改自己的角色，也不能禁用自己的账号。
- 至少保留一名启用的管理员：禁用或降级最后一名启用管理员时返回 `409`。
- 密码非空时长度仍需 ≥ 6。

响应：`200` + `UserDto`；账号不存在返回 `404`。

### `DELETE /api/users/{id}`

删除账号，返回 `204`。不能删除自己的账号，也不能删除最后一名启用的管理员（`400`，`{ "detail": "…" }`）；账号不存在返回 `404`。

## 8. 楼栋与楼层

楼栋与楼层是教室位置的可选值来源；教室保存的是楼栋、楼层**名称快照**，因此重命名会同步更新所属教室。

### `GET /api/buildings`

任意登录用户。按楼栋名称、楼层名称升序：

```json
[
  {
    "id": "…",
    "name": "A楼",
    "floors": [
      { "id": "…", "name": "1F" },
      { "id": "…", "name": "2F" }
    ]
  }
]
```

### `POST /api/buildings`

仅 `admin`。

```json
{ "name": "A楼" }
```

名称必填、≤ 60 字符、全局唯一。响应：`201 Created`，`Location: /api/buildings/{id}`，返回 `{ id, name }`。重名返回 `409`。

### `PUT /api/buildings/{buildingId}`

仅 `admin`。重命名为新名称时同步更新该楼栋下所有教室的 `building` 字段。名称未变化返回 `204`；重名返回 `409`；不存在返回 `404`。

### `DELETE /api/buildings/{buildingId}`

仅 `admin`。楼栋下仍有教室时返回 `409` 并提示数量；成功返回 `204`。删除楼栋会级联删除其楼层。

### `POST /api/buildings/{buildingId}/floors`

仅 `admin`。

```json
{ "name": "1F" }
```

同一楼栋内楼层名称唯一。响应：`201 Created`，`Location: /api/floors/{id}`，返回 `{ id, buildingId, name }`。

### `PUT /api/floors/{floorId}`

仅 `admin`。重命名时同步更新楼栋内该楼层下所有教室的 `floor` 字段。返回 `204`。

### `DELETE /api/floors/{floorId}`

仅 `admin`。楼层下仍有教室时返回 `409`；成功返回 `204`。

## 9. 教室

### `GET /api/rooms`

任意登录用户。返回控制台所需的完整教室树，每个教室按其唯一中控连接展开控制点与状态快照：

```json
[
  {
    "id": "…",
    "name": "R101",
    "building": "A楼",
    "floor": "1F",
    "controller": {
      "id": "…",
      "name": "Crestron 中控",
      "adapterType": "crestron-cip",
      "host": "192.168.1.20",
      "port": 41794,
      "ipid": 3,
      "enabled": true,
      "isOnline": true,
      "lastError": null,
      "joins": [
        {
          "id": "…",
          "name": "上课",
          "sendJoinNumber": 5,
          "stateJoinNumber": 6,
          "enableJoinNumber": 0,
          "showConfirmationPopup": true,
          "stateSnapshot": { "digitalValue": false, "lastReportedAtUtc": null },
          "enableSnapshot": null
        }
      ]
    }
  }
]
```

说明：

- `joins` 仅包含数字控制点，按 `sortOrder` 升序、名称次序排列。
- `stateSnapshot.digitalValue` 为 `false` 表示低电平/关闭；从未上报过的 Join 同样返回 `false`，`lastReportedAtUtc` 为 `null`。
- 未配置使能 Join（`enableJoinNumber <= 0`）时 `enableSnapshot` 为 `null`。
- 未接入中控的教室 `controller` 为 `null`。
- 教室按名称升序返回；分组与排序由前端完成。

### `POST /api/rooms`

仅 `admin`。创建教室，楼栋与楼层必须已在配置页定义。

```json
{ "name": "R101", "building": "A楼", "floor": "1F" }
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `name` | string | 必填，≤ 120 字符 |
| `building` | string | 必填，≤ 60 字符，必须存在于 `/api/buildings` |
| `floor` | string | 必填，≤ 60 字符，必须属于该楼栋 |

响应：`201 Created`，`Location: /api/rooms/{id}`。楼栋/楼层未定义时返回 `400`。

### `PUT /api/rooms/{roomId}`

仅 `admin`。请求体同创建，返回 `204`；教室不存在返回 `404`。

### `DELETE /api/rooms/{roomId}`

仅 `admin`。删除教室时一并删除其控制点、状态快照，以及指向这些控制点的定时任务与执行记录。返回 `204`。

## 10. 中控连接

### `GET /api/controller-types`

任意登录用户。返回可选的适配器类型；界面只允许从中选择，中控显示名称由类型决定：

```json
[
  {
    "key": "crestron-cip",
    "displayName": "Crestron 中控",
    "description": "CIP 协议 · Crestron 中控"
  }
]
```

协议细节见 [controller-protocols/](./controller-protocols/README.md)。

### `POST /api/rooms/{roomId}/controller`

仅 `admin`。为教室接入一台中控；每间教室最多一台。

```json
{
  "adapterType": "crestron-cip",
  "host": "192.168.1.20",
  "port": 41794,
  "ipid": 3
}
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `adapterType` | string | 必须为 `/api/controller-types` 中的 `key` |
| `host` | string | 必填，≤ 255 字符 |
| `port` | int | 1–65535，CIP Raw 默认 41794 |
| `ipid` | int | 0–255 |

响应：`201 Created`，`Location: /api/controllers/{id}`：

```json
{
  "id": "…",
  "roomId": "…",
  "name": "Crestron 中控",
  "adapterType": "crestron-cip",
  "host": "192.168.1.20",
  "port": 41794,
  "ipid": 3,
  "enabled": true
}
```

教室已有中控时返回 `409`。新接入的中控默认启用，后台连接管理器在 5 秒内开始连接，并在失败时指数退避重试（2 秒起，上限 30 秒）。

### `PUT /api/controllers/{controllerId}`

仅 `admin`。更新连接参数与启用状态，返回 `204`。`enabled=false` 会停止后台连接；参数变化会重建连接。

```json
{
  "adapterType": "crestron-cip",
  "host": "192.168.1.21",
  "port": 41794,
  "ipid": 3,
  "enabled": true
}
```

### `DELETE /api/controllers/{controllerId}`

仅 `admin`。删除中控及其控制点、状态快照和相关定时任务，返回 `204`。

## 11. 数字控制点

控制点（Join 映射）把一个界面按钮关联到中控的发送 Join、状态接收 Join 和可选使能 Join。当前仅支持数字（高/低）Join。

### `POST /api/controllers/{controllerId}/joins`

仅 `admin`。

```json
{
  "name": "上课",
  "sendJoinNumber": 5,
  "stateJoinNumber": 6,
  "enableJoinNumber": 0,
  "showConfirmationPopup": true
}
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `name` | string | 必填，≤ 120 字符 |
| `sendJoinNumber` | int | 1–32768，同一中控内唯一 |
| `stateJoinNumber` | int | 1–32768 |
| `enableJoinNumber` | int \| null | `0` 或 1–32768；`0`/`null` 表示不设置，默认允许操作 |
| `showConfirmationPopup` | bool | 默认 `true`；控制台发送指令与删除控制点前是否二次确认 |

新控制点自动排到列表末尾（`sortOrder` = 当前最大值 + 1）。响应：`201 Created`，`Location: /api/joins/{id}`：

```json
{
  "id": "…",
  "controllerId": "…",
  "name": "上课",
  "sendJoinNumber": 5,
  "stateJoinNumber": 6,
  "enableJoinNumber": 0,
  "showConfirmationPopup": true
}
```

发送 Join 已被占用返回 `409`。

### `PUT /api/joins/{mappingId}`

仅 `admin`。请求体与创建相同，返回 `204`；发送 Join 与其他控制点冲突时返回 `409`。

### `DELETE /api/joins/{mappingId}`

仅 `admin`。删除控制点，同时删除指向它的定时任务与执行记录，返回 `204`。

### `PUT /api/controllers/{controllerId}/joins/order`

仅 `admin`。配置页拖拽排序后提交该中控**全部**控制点的新顺序，必须不重不漏：

```json
{ "mappingIds": ["…第一个…", "…第二个…", "…第三个…"] }
```

返回 `204`。中控没有控制点时返回 `404`；集合不完整或包含未知 ID 时返回 `400`。

### `POST /api/joins/{mappingId}/copy`

仅 `admin`。把单个控制点复制到其他教室的中控，复制名称、发送/状态/使能 Join 与确认设置；目标上发送 Join 相同的既有控制点会被删除（连同其定时任务）。

```json
{ "controllerIds": ["…目标中控1…", "…目标中控2…"] }
```

响应：`200`：

```json
{ "copiedCount": 2 }
```

至少需要一个不同于源中控的目标（`400`）；目标不存在返回 `404`。

### `POST /api/controllers/{controllerId}/joins/copy-all`

仅 `admin`。把该中控的全部控制点按当前顺序复制到目标中控；目标教室原有控制点会先全部删除（连同其定时任务）。

```json
{ "controllerIds": ["…目标中控…"] }
```

响应：`200`：

```json
{ "copiedCount": 1, "controlPointCount": 6 }
```

源教室没有控制点返回 `409`。

## 12. 控制指令

### `POST /api/joins/{mappingId}/pulse`

`operator` 或 `admin`。向发送 Join 下发一次脉冲并请求中控刷新状态；指令写入连接后返回 `202 Accepted`，设备实际状态以后续上报的 Join 值为准（通过 SignalR 推送，或重新拉取 `GET /api/rooms`）。

失败语义：

| 状态 | 场景 |
| --- | --- |
| `404` | 控制点不存在 |
| `503` | 中控离线或未连接；连接在发送过程中断开 |
| `409` | 控制点配置了使能 Join 且该 Join 当前不是高电平 |

使能规则：

- 使能 Join 为 `0`（未配置）时始终允许人工操作。
- 配置后需中控上报高电平才允许操作；每次建立连接后使能状态先重置为低电平，等待设备重新上报。
- 使能 Join 只限制人工操作，不影响定时任务。

## 13. 定时任务

一个定时任务可作用于多间教室的控制点（界面按同名控制点对齐），所有目标共享 `groupId`，每个目标各自拥有独立执行记录。任务按服务器本地时间执行。

### 任务定义请求

创建与修改共用同一结构：

```json
{
  "joinMappingIds": ["…控制点1…", "…控制点2…"],
  "name": "早间开机",
  "kind": "Weekly",
  "runAtLocal": null,
  "weekdayMask": 62,
  "localTime": "08:00:00"
}
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `joinMappingIds` | GUID[] | 至少一项；可跨教室；重复项自动去重 |
| `name` | string | 必填，≤ 120 字符 |
| `kind` | string | `Once`（一次性）或 `Weekly`（按周重复） |
| `runAtLocal` | string \| null | `kind=Once` 时必填，服务器本地墙上时间（如 `2026-11-24T08:00:00`），必须晚于当前时间；`Weekly` 时忽略 |
| `weekdayMask` | int | `kind=Weekly` 时必填，1–127 位掩码 |
| `localTime` | string | `kind=Weekly` 时必填，`HH:mm:ss`，范围 `00:00:00`–`23:59:59` |

**星期位掩码**：位号与 `DayOfWeek` 一致，从周日开始。

| 位 | 1 | 2 | 4 | 8 | 16 | 32 | 64 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 星期 | 周日 | 周一 | 周二 | 周三 | 周四 | 周五 | 周六 |

例如工作日为 `2+4+8+16+32 = 62`，每天为 `127`。

**响应结构**（创建 `201 Created`、修改 `200 OK`，`Location: /api/schedules/groups/{groupId}`）：

```json
{
  "groupId": "…",
  "targetIds": ["…目标1…", "…目标2…"],
  "name": "早间开机",
  "kind": "Weekly",
  "nextRunAtUtc": "2026-11-24T00:00:00+00:00"
}
```

### `GET /api/schedules`

任意登录用户。返回全部任务目标（按下一次执行时间升序），前端按 `groupId` 聚合展示：

```json
[
  {
    "id": "…",
    "groupId": "…",
    "joinMappingId": "…",
    "roomId": "…",
    "roomName": "R101",
    "joinName": "上课",
    "name": "早间开机",
    "enabled": true,
    "kind": "Weekly",
    "runAtUtc": null,
    "weekdayMask": 62,
    "localTime": "08:00:00",
    "nextRunAtUtc": "2026-11-24T00:00:00+00:00",
    "joinMapping": { "id": "…", "name": "上课" }
  }
]
```

`joinMapping` 为 `null` 表示控制点已不存在（历史数据），此时 `roomId` 也为 `null`。`roomName` 与 `joinName` 为创建时的名称快照。

### `PUT /api/schedules/groups/{groupId}`

`operator` 或 `admin`。以请求中的 `joinMappingIds` 覆盖整组目标：

- 保留仍在列表中的目标，更新名称、类型、时间与重复日。
- 移除的目标连同其执行记录一起删除。
- 新增的目标沿用该组当前的启用/暂停状态。

返回 `200`，结构同创建。`groupId` 不存在返回 `404`。

### `POST /api/schedules/groups/{groupId}/pause`

`operator` 或 `admin`。暂停整组：保留定义与历史，不再触发，`nextRunAtUtc` 置空。返回 `204`。

### `POST /api/schedules/groups/{groupId}/resume`

`operator` 或 `admin`。恢复整组并按当前时间重算下一次执行。一次性任务若执行时间已过，返回 `409`，需先修改执行时间。返回 `204`。

### `DELETE /api/schedules/groups/{groupId}`

`operator` 或 `admin`。删除整组任务及全部执行记录，返回 `204`。

### `DELETE /api/schedules/{scheduleId}`

`operator` 或 `admin`。仅删除单个目标（某间教室）及其执行记录，返回 `204`。

### `GET /api/schedules/{scheduleId}/executions`

任意登录用户。返回该目标的最近 100 条执行记录，按计划时间倒序：

```json
[
  {
    "id": "…",
    "scheduleId": "…",
    "scheduledAtUtc": "2026-11-24T00:00:00+00:00",
    "completedAtUtc": "2026-11-24T00:00:01+00:00",
    "status": "Succeeded",
    "error": null
  }
]
```

`status` 取值：`Pending`（执行中）、`Succeeded`、`Failed`。失败原因（如中控离线）写入 `error`，不自动补发。任务目标不存在时返回空数组。

## 14. 操作日志

### `GET /api/logs`

仅 `admin`。

| 查询参数 | 类型 | 说明 |
| --- | --- | --- |
| `category` | string | 可选，取值见下表；非法值返回 `400` |
| `from` | ISO 8601 时间 | 可选，起始时间（含）；晚于 `to` 时返回 `400` |
| `to` | ISO 8601 时间 | 可选，结束时间（含） |
| `limit` | int | 可选，仅用于主动限制返回条数（取最新记录）；不传时返回全部保留记录，无 500 条上限 |

未指定 `category`、`from`、`to` 时返回全部未过期记录。

| 分类 | 说明 |
| --- | --- |
| `authentication` | 登录、退出、修改密码；对象栏记录来源 IP |
| `configuration` | 站点、楼栋/楼层、教室、中控、控制点、账号变更 |
| `control` | 人工下发脉冲 |
| `schedule` | 定时任务创建、修改、暂停、恢复、删除 |
| `state` | 设备上报引起的电平变化（仅在真正变化时记录） |
| `connection` | 中控连接成功/断开 |

响应按发生时间倒序：

```json
[
  {
    "id": "…",
    "category": "control",
    "action": "发送 Join 5",
    "target": "教室「A楼 · 1F · R101」· 控制点「上课」",
    "actor": "operator1",
    "statusCode": 202,
    "occurredAtUtc": "2026-11-23T12:00:00+00:00"
  }
]
```

### `GET /api/logs/settings`

仅 `admin`。读取自动清理的日志保留天数；未保存过设置时返回默认值 `60`。

```json
{ "retentionDays": 60 }
```

### `PUT /api/logs/settings`

仅 `admin`。保存保留天数并立即按新设置清理过期记录。

```json
{ "retentionDays": 30 }
```

- `retentionDays` 取值 `0–3650`；`0` 表示永久保留。超出范围返回 `400`。
- 响应返回保存后的值，形如 `{ "retentionDays": 30 }`。
- 清理任务每天凌晨 3 点（服务器本地时间）执行，应用启动时也会清理一次；保留天数在「配置与接入 · 站点与外观」中设置。

## 15. 实时推送（SignalR）

- 地址：`/hubs/control`，使用与 REST 相同的 Cookie 会话。
- 客户端只需加入关心的教室分组，服务端按分组广播，避免无关教室的状态推送到所有终端。
- 若账号在长连接期间被禁用或删除，服务端在下次加入/离开分组时中止连接。

### 客户端调用

| 方法 | 参数 | 说明 |
| --- | --- | --- |
| `JoinRoom` | `roomId: string` | 加入分组 `room:{roomId}` |
| `LeaveRoom` | `roomId: string` | 离开分组 `room:{roomId}` |

自动重连后必须重新调用 `JoinRoom`；服务端不保留连接级订阅。

### 服务端事件

**`joinStateChanged`**：某数字 Join 的最近值发生变化，或状态回放结束后被纠偏为低电平。

```json
{
  "controllerId": "…",
  "mappingIds": ["…控制点1…"],
  "joinNumber": 6,
  "value": true,
  "lastReportedAtUtc": "2026-11-23T12:00:00+00:00"
}
```

- `mappingIds` 包含状态接收或使能接收为该 `joinNumber` 的全部控制点。
- `value`：`true` 为高电平，`false` 为低电平（关闭）。
- 客户端应据此更新对应控制点的 `stateSnapshot` 或 `enableSnapshot`，无需整表刷新。

**`controllerStatusChanged`**：中控连接状态变化（连接成功、断开、重连失败）。

```json
{
  "controllerId": "…",
  "isOnline": false,
  "lastError": "Connection refused",
  "updatedAtUtc": "2026-11-23T12:00:00+00:00"
}
```

## 16. 数据模型

### Room

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | guid | 教室 ID |
| `name` | string | 教室名称（≤ 120） |
| `building` | string | 楼栋名称快照（≤ 60） |
| `floor` | string | 楼层名称快照（≤ 60） |
| `controller` | object \| null | 唯一中控连接 |

### Controller

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | guid | 中控 ID |
| `roomId` | guid | 所属教室 |
| `name` | string | 由适配器类型决定 |
| `adapterType` | string | 适配器键，如 `crestron-cip` |
| `host` / `port` | string / int | 中控网络地址 |
| `ipid` | int | CIP 标识（0–255） |
| `enabled` | bool | 连接是否启用 |
| `isOnline` | bool | 当前是否已连接 |
| `lastError` | string \| null | 最近一次连接错误 |
| `joins` | array | 数字控制点（`GET /api/rooms`） |

### JoinMapping

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | guid | 控制点 ID |
| `name` | string | 显示名称（≤ 120） |
| `sendJoinNumber` | int | 下发脉冲的 Join |
| `stateJoinNumber` | int | 状态反馈 Join |
| `enableJoinNumber` | int | 使能 Join，`0` 表示未配置 |
| `showConfirmationPopup` | bool | 操作前是否二次确认 |
| `stateSnapshot` | object | `{ digitalValue, lastReportedAtUtc }` |
| `enableSnapshot` | object \| null | 使能 Join 快照；未配置时为 `null` |

### ControlSchedule / ScheduleExecution

字段见[定时任务](#13-定时任务)。任务定义中的 `roomName`、`joinName` 为创建时的名称快照，控制点删除后 `joinMappingId` 与 `joinMapping` 可能为空，但历史执行记录仍保留可读信息。

### UserDto

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | guid | 账号 ID |
| `username` | string | 用户名 |
| `role` | string | `admin` / `operator` / `viewer` |
| `roleDisplayName` | string | 中文角色名 |
| `isActive` | bool | 是否启用 |
| `createdAt` | datetime | 创建时间（UTC） |

## 17. 业务规则与生命周期

- **接入顺序**：站点品牌 → 楼栋/楼层 → 教室 → 中控 → 控制点 → 自动任务。教室只能引用已定义的楼栋与楼层；重命名楼栋/楼层会同步更新教室快照，仍有教室时禁止删除楼栋/楼层。
- **一台中控对应一间教室**：唯一约束；接入第二台中控返回 `409`。
- **状态快照**：每个 `(控制器, Join 类型, Join 编号)` 只保存最近一次值。从未上报或服务启动后尚未收到回报时按低电平处理，不会出现“未知”状态；服务重启时清除全部在线标记并将快照重置为低电平。中控断开时保留最后一次状态与上报时间，重连后使能 Join 先重置为低电平、等待设备重新上报，普通状态 Join 由随后的状态回放刷新。
- **状态回放纠偏**：中控全量回放只上报高电平 Join；平台在回放结束标记后，把本次窗口内未上报的状态接收 Join 置为低电平并推送更新，使能 Join 不受影响。
- **删除级联**：删除控制点会删除其定时任务；删除中控会删除其控制点、快照和定时任务；删除教室会删除中控、控制点、快照和定时任务。执行记录随所属任务目标一起删除。
- **一次性任务**：必须设置未来时间。平台停机期间到期的一次性任务在下次启动时记为失败（`error` 说明未自动补发），不再触发。执行完成后自动停用。
- **每周任务**：按服务器本地时间与星期掩码重复；每次执行完成后按完成时刻重算下一次。恢复暂停任务时同样重算。
- **使能 Join**：只限制 `POST /api/joins/{mappingId}/pulse` 等人工操作，定时任务不受限，只要求中控在线。
- **密码与账号**：密码使用 PBKDF2-SHA256（210,000 次迭代、每账号独立随机盐）存储；登录时对不存在账号也执行同样的 KDF 与固定时间比较，避免通过响应差异判断账号是否存在。初始管理员仅在数据库无任何账号时创建。

## 版本与兼容性

- API 的路径、字段名与枚举字符串是前后端和历史数据共享的契约，修改时需同步更新前端类型（[types.ts](../web/src/types.ts)）与本文档。
- 数据库为 SQLite 或 PostgreSQL，启动时自动补齐增量结构（见 [SchemaUpgrader.cs](../backend/RoomControl.Api/Data/SchemaUpgrader.cs)）；接口层不暴露数据库差异。
- 老的模拟/串口控制点保留在数据库中但不再通过 API 展示或操作，其定时任务会在启动时清理。
