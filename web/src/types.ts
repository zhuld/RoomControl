// 前端视图状态、API 数据结构和配置选项的共享类型定义。
export type ScheduleKind = 'Once' | 'Weekly'
// 工作区按操作员任务流划分，而不是按页面划分。
export type Workspace = 'overview' | 'operate' | 'settings' | 'audit'
export type Density = 'comfortable' | 'compact'
export type FeedbackTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral'
export type UserRole = 'admin' | 'operator' | 'viewer'
export type SettingsSection =
  | 'appearance'
  | 'branding'
  | 'locations'
  | 'rooms'
  | 'controllers'
  | 'automation'
  | 'users'

// 从操作台或总览跳转自动任务时携带的上下文，token 用于重复触发同一目标。
export interface AutomationTarget {
  token: number
  roomId?: string
  joinId?: string
  scheduleId?: string
}

export type ReceiptStatus = 'sending' | 'sent' | 'failed'

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

export const userRoles: { value: UserRole; label: string; description: string }[] = [
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
]

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
