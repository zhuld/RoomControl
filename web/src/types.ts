// 前端视图状态、API 数据结构和配置选项的共享类型定义。
export type ScheduleKind = (typeof scheduleKinds)[number]['value']
// 工作区按操作员任务流划分，而不是按页面划分。
export type Workspace = (typeof workspaces)[number]['value']
export type Density = (typeof densityOptions)[number]['value']
export type FeedbackTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral'
// 角色取值由 userRoles 单一定义推导，避免类型与选项两处维护。
export type UserRole = (typeof userRoles)[number]['value']
export type SettingsSection = (typeof settingsSections)[number]['value']

// 从操作台或总览跳转自动任务时携带的上下文，token 用于重复触发同一目标。
export interface AutomationTarget {
  token: number
  roomId?: string
  joinId?: string
  scheduleId?: string
}

export type ReceiptStatus = (typeof receiptStatuses)[number]['value']

// 命令回执是当前浏览器会话内可追踪的操作记录，与后端审计日志互补。
export interface CommandReceipt {
  id: string
  at: string
  roomId: string
  roomName: string
  joinId: string
  joinName: string
  status: ReceiptStatus
  message: string | null
}

export interface User {
  id: string
  username: string
  role: UserRole
  roleDisplayName: string
  isActive: boolean
  createdAt: string
}

export interface CurrentUser {
  username: string
  role: UserRole
}

export interface AuditLog {
  id: string
  category: string
  action: string
  target: string
  actor: string
  statusCode: number
  occurredAtUtc: string
}

export interface LoginAccount {
  username: string
  role: UserRole
}

// 角色的唯一定义来源：新增或调整角色只需修改这里。
export const userRoles = [
  {
    value: 'admin',
    label: '管理员',
    description: '拥有全部权限，可管理用户、站点配置与教室中控。',
  },
  {
    value: 'operator',
    label: '操作员',
    description: '仅可查看状态并发送控制指令，不可进入配置页管理用户。',
  },
  {
    value: 'viewer',
    label: '观看员',
    description: '仅可查看教室状态与中控在线情况，不可发送指令或修改配置。',
  },
] as const

// 配置步骤的唯一定义来源：数组顺序即侧栏顺序，新增或调整步骤只需修改这里。
export const settingsSections = [
  {
    value: 'branding',
    eyebrow: '站点设置',
    title: '站点设置',
    icon: 'globe',
    description: '维护登录页与侧栏使用的名称、Logo 与操作日志保留策略。',
  },
  {
    value: 'locations',
    eyebrow: '教室层级',
    title: '楼栋与楼层',
    icon: 'building',
    description: '先定义楼栋与楼层，教室只能从已配置项中选择；重命名会同步更新已有教室。',
  },
  {
    value: 'rooms',
    eyebrow: '教室',
    title: '教室管理',
    icon: 'room',
    description: '维护教室名称与所在位置。自动任务按服务器本地时间执行。',
  },
  {
    value: 'controllers',
    eyebrow: '中控与设备',
    title: '中控与设备',
    icon: 'connection',
    description: '为教室接入中控并配置数字 Join 映射；保存后后台会自动重连并同步状态。',
  },
  {
    value: 'automation',
    eyebrow: '自动任务',
    title: '自动任务',
    icon: 'automation',
    description: '一次任务可覆盖多间教室的同名控制点，也可随时修改时间、范围与目标。',
  },
  {
    value: 'manual-tasks',
    eyebrow: '手动任务',
    title: '手动任务',
    icon: 'play',
    description: '把多间教室的同名控制点聚合为一个按钮，显示在系统总览，点击即批量下发。',
  },
  {
    value: 'users',
    eyebrow: '用户与权限',
    title: '用户与权限',
    icon: 'users',
    description: '管理员可管理配置与账号，操作员可下发指令，观看员仅可查看状态。',
  },
  {
    value: 'appearance',
    eyebrow: '显示偏好',
    title: '外观与显示',
    icon: 'palette',
    description: '调整主题与显示密度；仅保存在当前浏览器，管理员、操作员和观看员均可修改。',
  },
] as const

export const settingsSectionMeta = Object.fromEntries(
  settingsSections.map((section) => [section.value, section] as const),
) as Record<SettingsSection, (typeof settingsSections)[number]>

// 工作区的唯一定义来源：数组顺序即侧栏顺序。
export interface WorkspaceDefinition {
  readonly value: string
  readonly label: string
  readonly icon: string
}

export const workspaces = [
  { value: 'overview', label: '系统总览', icon: 'overview' },
  { value: 'operate', label: '教室操作', icon: 'operate' },
  { value: 'settings', label: '系统配置', icon: 'settings' },
  { value: 'audit', label: '操作记录', icon: 'audit' },
] as const satisfies readonly WorkspaceDefinition[]

export const workspaceMeta = Object.fromEntries(
  workspaces.map((item) => [item.value, item] as const),
) as Record<Workspace, WorkspaceDefinition>

// 重复方式的唯一定义来源。
export const scheduleKinds = [
  { value: 'Weekly', label: '每周重复' },
  { value: 'Once', label: '一次性' },
] as const

// 显示密度的唯一定义来源。
export const densityOptions = [
  { value: 'comfortable', label: '标准' },
  { value: 'compact', label: '紧凑' },
] as const

// 命令回执状态的唯一定义来源：文案与色调都在这里维护。
export const receiptStatuses = [
  { value: 'sending', label: '发送中', tone: 'info' },
  { value: 'sent', label: '已发送', tone: 'success' },
  { value: 'failed', label: '发送失败', tone: 'danger' },
] as const satisfies readonly { value: string; label: string; tone: FeedbackTone }[]

// 主题的唯一定义来源：新增或调整主题只需修改这里。
export const themeOptions = [
  { value: 'pine', label: '松绿', swatch: 'var(--swatch-pine)' },
  { value: 'sand', label: '暖砂', swatch: 'var(--swatch-sand)' },
  { value: 'ocean', label: '海蓝', swatch: 'var(--swatch-ocean)' },
  { value: 'iris', label: '紫藤', swatch: 'var(--swatch-iris)' },
  { value: 'rose', label: '玫瑰', swatch: 'var(--swatch-rose)' },
  { value: 'graphite', label: '石墨（深色）', swatch: 'var(--swatch-graphite)' },
] as const

export type ThemeName = (typeof themeOptions)[number]['value']

// 未知或缺失的主题名回退到默认主题。
function isThemeName(value: string | null): value is ThemeName {
  return value !== null && themeOptions.some((option) => option.value === value)
}

export function resolveThemeName(value: string | null): ThemeName {
  return isThemeName(value) ? value : 'pine'
}

export interface JoinSnapshot {
  digitalValue: boolean
  lastReportedAtUtc: string | null
}

export interface JoinMapping {
  id: string
  name: string
  sendJoinNumber: number
  stateJoinNumber: number
  enableJoinNumber: number
  stateSnapshot: JoinSnapshot
  enableSnapshot: JoinSnapshot | null
  showConfirmationPopup: boolean
}

export interface ControllerInfo {
  id: string
  name: string
  adapterType: string
  host: string
  port: number
  ipid: number
  enabled: boolean
  isOnline: boolean
  lastError: string | null
  joins: JoinMapping[]
}

export interface Room {
  id: string
  name: string
  building: string
  floor: string
  controller: ControllerInfo | null
}

export interface Schedule {
  id: string
  groupId: string | null
  joinMappingId: string | null
  roomId: string | null
  name: string
  kind: ScheduleKind
  enabled: boolean
  roomName: string
  joinName: string
  runAtUtc: string | null
  weekdayMask: number
  localTime: string
  nextRunAtUtc: string | null
  joinMapping: Pick<JoinMapping, 'id' | 'name'> | null
}

export interface ScheduleExecution {
  id: string
  scheduleId: string
  scheduledAtUtc: string
  completedAtUtc: string | null
  status: string
  error: string | null
}

export interface ManualTaskTarget {
  id: string
  joinMappingId: string | null
  roomId: string | null
  roomName: string
  joinName: string
  controllerId: string | null
  isOnline: boolean
}

export interface ManualTask {
  id: string
  name: string
  joinName: string
  confirmBeforeRun: boolean
  sortOrder: number
  targets: ManualTaskTarget[]
}

export interface ManualTaskRunTargetResult {
  targetId: string
  roomName: string
  joinName: string
  status: string
  error: string | null
}

export interface ManualTaskRunResult {
  runId: string
  taskName: string
  startedAtUtc: string
  completedAtUtc: string
  succeededCount: number
  failedCount: number
  targets: ManualTaskRunTargetResult[]
}

export interface ManualTaskExecution {
  id: string
  manualTaskId: string
  startedAtUtc: string
  completedAtUtc: string | null
  succeededCount: number
  failedCount: number
  status: string
  error: string | null
}

export interface Branding {
  displayName: string
  subtitle: string
  logoDataUrl: string | null
}

export interface FloorOption {
  id: string
  name: string
}

export interface BuildingOption {
  id: string
  name: string
  floors: FloorOption[]
}

export interface ControllerTypeOption {
  key: string
  displayName: string
  description: string
}

export const defaultBranding: Branding = {
  displayName: 'RoomControl',
  subtitle: '教室中控平台',
  logoDataUrl: null,
}
