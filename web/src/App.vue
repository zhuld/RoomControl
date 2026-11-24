<script setup lang="ts">
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr'
import { computed, nextTick, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue'
import { readJson, request, toErrorMessage } from './api'
import LogsPage from './components/LogsPage.vue'
import OperateWorkspace from './components/OperateWorkspace.vue'
import OverviewWorkspace from './components/OverviewWorkspace.vue'
import SettingsPage from './components/SettingsPage.vue'
import TaskRail from './components/TaskRail.vue'
import ConfirmDialog from './components/ui/ConfirmDialog.vue'
import UiIcon from './components/ui/UiIcon.vue'
import { useConfirmDialog } from './lib/confirm'
import { actionSlotKey, compactModeKey } from './lib/layout'
import {
  canAccessSettingsSection,
  defaultSettingsSection,
  settingsNavLabel,
} from './lib/settings'
import BrandLogo from './components/BrandLogo.vue'
import {
  defaultBranding,
  resolveThemeName,
  userRoles,
  type AutomationTarget,
  type Branding,
  type CommandReceipt,
  type CurrentUser,
  type Density,
  type JoinMapping,
  type LoginAccount,
  type Room,
  type Schedule,
  type SettingsSection,
  type ThemeName,
  type UserRole,
  type Workspace,
} from './types'

// 根组件维护会话、连接、当前工作区与命令回执；页面只负责各自任务流。
const authenticated = ref(false)
const checkingAuth = ref(true)
const apiReady = ref(false)
const startupFailed = ref(false)
const busy = ref(false)
const rooms = ref<Room[]>([])
const schedules = ref<Schedule[]>([])
const selectedRoomId = ref('')
const errorMessage = ref('')
const successMessage = ref('')
const snapshotError = ref('')
const password = ref('')
const username = ref('')
const loginAccounts = ref<LoginAccount[]>([])
const currentUser = ref<CurrentUser | null>(null)
const workspace = ref<Workspace>('overview')
const settingsSection = ref<SettingsSection>('branding')
const settingsFocusRoomId = ref('')
const automationTarget = ref<AutomationTarget | null>(null)
const branding = ref<Branding>({ ...defaultBranding })
const railOpen = ref(false)
const railCollapsed = ref(localStorage.getItem('roomcontrol.rail.collapsed') === '1')
const workspaceScroll = ref<HTMLElement | null>(null)
const roomJumpQuery = ref('')
let automationToken = 0

const canOperate = computed(() => currentUser.value?.role !== 'viewer')

const workspaceMeta: Record<Workspace, string> = {
  overview: '系统总览',
  operate: '教室操作',
  settings: '配置与接入',
  audit: '操作记录',
}

const activeWorkspaceLabel = computed(() =>
  workspace.value === 'settings'
    ? settingsNavLabel(currentUser.value?.role)
    : workspaceMeta[workspace.value],
)

const theme = ref<ThemeName>('pine')
const density = ref<Density>('comfortable')
// 紧凑模式下各工作区把页头操作传送到顶部栏，页头整体隐藏。
const actionSlot = ref<HTMLElement | null>(null)
provide(
  compactModeKey,
  computed(() => density.value === 'compact'),
)
provide(actionSlotKey, actionSlot)
const debugEnabled = ref(localStorage.getItem('roomcontrol.debug') === '1')
const currentDateTime = ref(new Date())
let clockInterval: ReturnType<typeof setInterval> | undefined
let toastTimeout: ReturnType<typeof setTimeout> | undefined
// 自动任务在后台执行后没有推送事件；轮询计划数据以清除过期的逾期提示。
const SCHEDULE_REFRESH_MS = 15_000
let scheduleRefreshTimer: ReturnType<typeof setInterval> | undefined

const hubState = ref<'connecting' | 'connected' | 'disconnected'>('disconnected')
const lastHubEventAt = ref<string | null>(null)
let hub: HubConnection | null = null

const receipts = ref<CommandReceipt[]>([])
const RECEIPT_STORAGE_KEY = 'roomcontrol.receipts'

const { confirmation, confirmAction, resolveConfirmation } = useConfirmDialog()

const selectedLoginRole = computed(() => {
  const account = loginAccounts.value.find((item) => item.username === username.value)
  return userRoles.find((role) => role.value === account?.role) ?? null
})

const configuredRoomCount = computed(() => rooms.value.filter((room) => room.controller).length)
const onlineRoomCount = computed(
  () => rooms.value.filter((room) => room.controller?.isOnline).length,
)
const offlineRoomCount = computed(
  () => rooms.value.filter((room) => room.controller && !room.controller.isOnline).length,
)
const unconfiguredRoomCount = computed(() => rooms.value.filter((room) => !room.controller).length)
const overdueScheduleCount = computed(() => {
  // 同一任务覆盖多间教室时，接口会返回多条目标记录；按 GroupId 合并后计数。
  const groups = new Set<string>()
  for (const schedule of schedules.value) {
    if (
      schedule.enabled &&
      schedule.nextRunAtUtc !== null &&
      new Date(schedule.nextRunAtUtc).getTime() < currentDateTime.value.getTime() - 60_000
    ) {
      groups.add(schedule.groupId ?? schedule.id)
    }
  }
  return groups.size
})
const attentionCount = computed(
  () => offlineRoomCount.value + unconfiguredRoomCount.value + overdueScheduleCount.value,
)

const roomJumpOptions = computed(() =>
  rooms.value
    .map((room) => roomJumpLabel(room))
    .sort((left, right) => left.localeCompare(right, 'zh-CN', { numeric: true })),
)

const clockDate = computed(() =>
  currentDateTime.value.toLocaleDateString('zh-CN', { month: 'long', day: 'numeric', weekday: 'short' }),
)
const clockTime = computed(() => currentDateTime.value.toLocaleTimeString('zh-CN', { hour12: false }))

const onlineTone = computed(() => {
  if (!configuredRoomCount.value) return 'neutral'
  return onlineRoomCount.value === configuredRoomCount.value ? 'success' : 'warning'
})

function applyTheme() {
  document.documentElement.setAttribute('data-theme', theme.value)
  const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')
  if (meta) {
    meta.content = getComputedStyle(document.documentElement).getPropertyValue('--rail').trim()
  }
}

function applyDensity() {
  document.documentElement.setAttribute('data-density', density.value)
}

watch(
  theme,
  (newTheme) => {
    localStorage.setItem('theme', newTheme)
    applyTheme()
  },
)

watch(
  density,
  (newDensity) => {
    localStorage.setItem('roomcontrol.density', newDensity)
    applyDensity()
  },
)

watch(debugEnabled, (enabled) => {
  localStorage.setItem('roomcontrol.debug', enabled ? '1' : '0')
})

watch(workspace, (value) => {
  localStorage.setItem('roomcontrol.workspace', value)
  railOpen.value = false
  // 切换到总览或配置页时立即同步一次计划，避免显示已执行任务的旧时间。
  if (value === 'overview' || value === 'settings') void refreshSchedulesInBackground()
})

watch(railCollapsed, (collapsed) => {
  localStorage.setItem('roomcontrol.rail.collapsed', collapsed ? '1' : '0')
})

watch(selectedRoomId, (roomId) => {
  if (roomId) localStorage.setItem('roomcontrol.room', roomId)
})

// 回执只保留当前会话；刷新后仍可核对最近的发送结果。
watch(
  receipts,
  (value) => {
    try {
      sessionStorage.setItem(RECEIPT_STORAGE_KEY, JSON.stringify(value))
    } catch {
      // 存储不可用时仍保留内存中的回执。
    }
  },
  { deep: true },
)

watch(currentUser, (user) => {
  if (!user) return
  // 观看员可进入配置页调整外观，但不能访问操作台。
  if (user.role === 'viewer' && workspace.value === 'operate') {
    workspace.value = 'overview'
  }
  // 角色变化后把当前配置步骤收敛到该角色可访问的步骤。
  if (!canAccessSettingsSection(settingsSection.value, user.role)) {
    settingsSection.value = defaultSettingsSection(user.role)
  }
})

function canAccess(target: Workspace, role: UserRole | undefined) {
  if (!role) return target === 'overview'
  // 操作记录与配置页对所有已登录角色开放（配置页内部再按步骤限制）；教室操作仅非只读角色可用。
  if (target === 'operate') return role !== 'viewer'
  return true
}

function navigate(target: Workspace) {
  if (!canAccess(target, currentUser.value?.role)) return
  if (target === 'settings' || target === 'audit') settingsFocusRoomId.value = ''
  if (target === 'settings' && !canAccessSettingsSection(settingsSection.value, currentUser.value?.role)) {
    settingsSection.value = defaultSettingsSection(currentUser.value?.role)
  }
  workspace.value = target
  void nextTick(() => workspaceScroll.value?.scrollTo({ top: 0 }))
}

function openRoom(roomId: string) {
  if (!canOperate.value) return
  selectedRoomId.value = roomId
  navigate('operate')
}

function openSettings(section: SettingsSection, roomId = '') {
  if (!canAccessSettingsSection(section, currentUser.value?.role)) return
  settingsSection.value = section
  settingsFocusRoomId.value = roomId
  workspace.value = 'settings'
  void nextTick(() => workspaceScroll.value?.scrollTo({ top: 0 }))
}

function openAutomation(target?: { roomId?: string; joinId?: string; scheduleId?: string }) {
  if (!canOperate.value) return
  automationToken += 1
  automationTarget.value = { ...target, token: automationToken }
  settingsSection.value = 'automation'
  workspace.value = 'settings'
  void nextTick(() => workspaceScroll.value?.scrollTo({ top: 0 }))
}

function scheduleCommand(room: Room, join: JoinMapping) {
  openAutomation({ roomId: room.id, joinId: join.id })
}

function handleAutomationChanged() {
  void loadSnapshot()
}

function handleSettingsSection(section: SettingsSection) {
  settingsSection.value = canAccessSettingsSection(section, currentUser.value?.role)
    ? section
    : defaultSettingsSection(currentUser.value?.role)
  void nextTick(() => workspaceScroll.value?.scrollTo({ top: 0 }))
}

function roomJumpLabel(room: Room) {
  return `${room.building || '未分配楼栋'} / ${room.floor || '未分配楼层'} / ${room.name}`
}

function jumpToRoom() {
  const query = roomJumpQuery.value.trim().toLocaleLowerCase()
  if (!query) return
  const room =
    rooms.value.find((item) => roomJumpLabel(item).toLocaleLowerCase() === query) ??
    rooms.value.find((item) => roomJumpLabel(item).toLocaleLowerCase().includes(query))
  if (!room) {
    showToast('未找到匹配的教室。', true)
    return
  }
  roomJumpQuery.value = ''
  openRoom(room.id)
}

function dismissToast() {
  if (toastTimeout !== undefined) {
    clearTimeout(toastTimeout)
    toastTimeout = undefined
  }
  errorMessage.value = ''
  successMessage.value = ''
}

function showToast(message: string, isError: boolean) {
  if (toastTimeout !== undefined) clearTimeout(toastTimeout)
  errorMessage.value = isError ? message : ''
  successMessage.value = isError ? '' : message
  toastTimeout = setTimeout(dismissToast, 8000)
}

function showError(error: unknown) {
  showToast(toErrorMessage(error), true)
}

function showSuccess(message: string) {
  showToast(message, false)
}

function loadStoredReceipts() {
  try {
    const parsed = JSON.parse(sessionStorage.getItem(RECEIPT_STORAGE_KEY) ?? '[]') as CommandReceipt[]
    receipts.value = parsed
      .filter((receipt) => receipt && (receipt.status === 'sent' || receipt.status === 'failed'))
      .slice(0, 50)
  } catch {
    receipts.value = []
  }
}

function clearReceipts() {
  receipts.value = []
  try {
    sessionStorage.removeItem(RECEIPT_STORAGE_KEY)
  } catch {
    // 忽略存储失败。
  }
}

async function loadBranding() {
  try {
    branding.value = await readJson<Branding>('/api/branding')
  } catch {
    branding.value = { ...defaultBranding }
  }
  applyBrandingTitle()
}

function applyBrandingTitle() {
  document.title = branding.value.subtitle
    ? `${branding.value.displayName} · ${branding.value.subtitle}`
    : branding.value.displayName
}

async function loadRooms() {
  const storedRoom = localStorage.getItem('roomcontrol.room') ?? ''
  rooms.value = await readJson<Room[]>('/api/rooms')
  if (!rooms.value.some((room) => room.id === selectedRoomId.value)) {
    selectedRoomId.value =
      rooms.value.find((room) => room.id === storedRoom)?.id ?? rooms.value[0]?.id ?? ''
  }
}

async function loadSchedules() {
  schedules.value = await readJson<Schedule[]>('/api/schedules')
}

// 计划任务由后台执行，前端没有实时事件；定期刷新以更新下次执行时间与逾期状态。
async function refreshSchedulesInBackground() {
  if (!authenticated.value || document.hidden) return
  try {
    await loadSchedules()
  } catch {
    // 后台刷新失败时保留现有数据，下一次轮询继续重试。
  }
}

function handleScheduleVisibilityChange() {
  if (!document.hidden) void refreshSchedulesInBackground()
}

async function loadCurrentUser() {
  try {
    const me = await readJson<{ username: string; role: UserRole }>('/api/auth/me')
    currentUser.value = { username: me.username, role: me.role }
  } catch {
    currentUser.value = null
  }
}

async function loadLoginAccounts() {
  try {
    loginAccounts.value = await readJson<LoginAccount[]>('/api/auth/accounts')
  } catch {
    loginAccounts.value = []
  }
}

// 后端未完成启动（含数据库迁移与初始化）前保持启动提示，轮询健康检查。
async function waitForBackend() {
  let attempts = 0
  while (!apiReady.value) {
    attempts += 1
    try {
      const response = await fetch('/health', { cache: 'no-store', signal: AbortSignal.timeout(5_000) })
      if (response.ok) {
        apiReady.value = true
        return
      }
    } catch {
      // 后端尚未监听端口，稍后重试。
    }
    if (attempts >= 12) return
    await new Promise((resolve) => setTimeout(resolve, 2_000))
  }
}

async function bootstrap() {
  checkingAuth.value = true
  startupFailed.value = false
  apiReady.value = false
  await waitForBackend()
  if (!apiReady.value) {
    startupFailed.value = true
    checkingAuth.value = false
    return
  }

  await loadBranding()
  try {
    await loadCurrentUser()
    authenticated.value = !!currentUser.value
    if (authenticated.value) {
      try {
        await loadSnapshot()
      } catch (error) {
        showError(error)
      }
      const stored = localStorage.getItem('roomcontrol.workspace')
      // 只恢复当前存在且有权访问的工作区，避免使用过期或无效的存储值。
      if (stored && stored in workspaceMeta && canAccess(stored as Workspace, currentUser.value?.role)) {
        workspace.value = stored as Workspace
      }
    }
  } catch {
    authenticated.value = false
  } finally {
    if (!authenticated.value) await loadLoginAccounts()
    checkingAuth.value = false
  }
}

async function loadSnapshot() {
  try {
    await Promise.all([loadRooms(), loadSchedules()])
    snapshotError.value = ''
  } catch (error) {
    snapshotError.value = toErrorMessage(error)
    throw error
  }
}

async function login() {
  busy.value = true
  try {
    await request('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username: username.value, password: password.value }),
    })
    password.value = ''
    await loadCurrentUser()
    if (!currentUser.value) throw new Error('登录状态无效，请重试。')
    authenticated.value = true
    dismissToast()
    try {
      await loadSnapshot()
    } catch (error) {
      showError(error)
    }
    clearReceipts()
    workspace.value = 'overview'
  } catch (error) {
    authenticated.value = false
    showError(error)
  } finally {
    busy.value = false
  }
}

// 登出时同步停止 SignalR 并清空会话数据，避免切换账号后显示旧状态。
async function logout() {
  if (
    !(await confirmAction('退出后需要重新登录才能继续操作，确定退出登录吗？', {
      title: '退出登录',
      actionLabel: '确认退出',
      tone: 'danger',
    }))
  ) {
    return
  }

  try {
    await request('/api/auth/logout', { method: 'POST' })
  } catch (error) {
    showError(error)
  } finally {
    await hub?.stop()
    hub = null
    hubState.value = 'disconnected'
    lastHubEventAt.value = null
    authenticated.value = false
    currentUser.value = null
    rooms.value = []
    schedules.value = []
    snapshotError.value = ''
    username.value = ''
    password.value = ''
    workspace.value = 'overview'
    clearReceipts()
    dismissToast()
    await loadLoginAccounts()
  }
}

async function refreshData() {
  busy.value = true
  try {
    await loadSnapshot()
    showSuccess('状态已刷新')
  } catch (error) {
    showError(error)
  } finally {
    busy.value = false
  }
}

async function handleRoomsChanged() {
  try {
    await loadSnapshot()
  } catch (error) {
    showError(error)
  }
}

function handleBrandingSaved(saved: Branding) {
  branding.value = saved
  applyBrandingTitle()
}

function handleThemeChanged(newTheme: ThemeName) {
  theme.value = newTheme
}

function handleSettingsNotify(payload: { message: string; isError: boolean }) {
  showToast(payload.message, payload.isError)
}

function updateReceipt(id: string, patch: Partial<CommandReceipt>) {
  receipts.value = receipts.value.map((receipt) =>
    receipt.id === id ? { ...receipt, ...patch } : receipt,
  )
}

function createReceiptId() {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

async function sendDigitalPulse(room: Room, join: JoinMapping) {
  // 控制点开启确认弹窗时先征得确认；关闭则直接下发指令。
  if (join.showConfirmationPopup !== false) {
    const confirmed = await confirmAction(`确认向「${room.name}」发送「${join.name}」指令？`, {
      title: '确认发送指令',
      actionLabel: '确认发送',
    })
    if (!confirmed) return
  }

  const receipt: CommandReceipt = {
    id: createReceiptId(),
    at: new Date().toISOString(),
    roomId: room.id,
    roomName: room.name,
    joinId: join.id,
    joinName: join.name,
    status: 'sending',
    message: null,
  }
  receipts.value = [receipt, ...receipts.value].slice(0, 50)

  try {
    await request(`/api/joins/${join.id}/pulse`, { method: 'POST' })
    updateReceipt(receipt.id, { status: 'sent', at: new Date().toISOString() })
    showSuccess(`已发送：${join.name}`)
  } catch (error) {
    const message = toErrorMessage(error)
    updateReceipt(receipt.id, { status: 'failed', message, at: new Date().toISOString() })
    showError(error)
  }
}

function applyJoinUpdate(event: {
  controllerId: string
  joinNumber: number
  value: boolean
  lastReportedAtUtc: string
}) {
  // 一个上报 Join 可能同时被映射为状态 Join 和使能 Join。
  lastHubEventAt.value = event.lastReportedAtUtc || new Date().toISOString()
  const controller = rooms.value.find((room) => room.controller?.id === event.controllerId)?.controller
  if (!controller) return
  const snapshot = {
    digitalValue: event.value,
    lastReportedAtUtc: event.lastReportedAtUtc,
  }
  for (const join of controller.joins) {
    if (join.stateJoinNumber === event.joinNumber) join.stateSnapshot = snapshot
    if (join.enableJoinNumber === event.joinNumber) join.enableSnapshot = snapshot
  }
}

async function connectToRoomHub(roomIds: string[]) {
  await hub?.stop()
  hub = null
  if (!roomIds.length || !authenticated.value) return

  hubState.value = 'connecting'
  // 单个连接订阅所有已加载房间；自动重连后必须重新加入 SignalR 分组。
  const connection = new HubConnectionBuilder().withUrl('/hubs/control').withAutomaticReconnect().build()
  connection.on('joinStateChanged', applyJoinUpdate)
  connection.on(
    'controllerStatusChanged',
    (event: {
      controllerId: string
      isOnline: boolean
      lastError: string | null
    }) => {
      lastHubEventAt.value = new Date().toISOString()
      const controller = rooms.value.find((room) => room.controller?.id === event.controllerId)?.controller
      if (controller?.id === event.controllerId) {
        if (event.isOnline && !controller.isOnline) {
          for (const join of controller.joins) join.enableSnapshot = null
        }
        controller.isOnline = event.isOnline
        controller.lastError = event.lastError
      }
    },
  )
  connection.onreconnecting(() => (hubState.value = 'connecting'))
  connection.onreconnected(async () => {
    hubState.value = 'connected'
    await Promise.all(roomIds.map((roomId) => connection.invoke('JoinRoom', roomId)))
  })
  connection.onclose(() => (hubState.value = 'disconnected'))

  try {
    await connection.start()
    await Promise.all(roomIds.map((roomId) => connection.invoke('JoinRoom', roomId)))
    hub = connection
    hubState.value = 'connected'
  } catch {
    hubState.value = 'disconnected'
  }
}

watch(
  () => `${authenticated.value}:${rooms.value.map((room) => room.id).join(',')}`,
  () => void connectToRoomHub(rooms.value.map((room) => room.id)),
)

onMounted(() => {
  // 主题与密度先应用，避免登录页出现闪烁。
  theme.value = resolveThemeName(localStorage.getItem('theme'))
  density.value = localStorage.getItem('roomcontrol.density') === 'compact' ? 'compact' : 'comfortable'
  applyTheme()
  applyDensity()
  loadStoredReceipts()
  void bootstrap()
  clockInterval = setInterval(() => {
    currentDateTime.value = new Date()
  }, 1000)
  scheduleRefreshTimer = setInterval(refreshSchedulesInBackground, SCHEDULE_REFRESH_MS)
  document.addEventListener('visibilitychange', handleScheduleVisibilityChange)
})

onBeforeUnmount(() => {
  if (toastTimeout !== undefined) clearTimeout(toastTimeout)
  if (clockInterval !== undefined) clearInterval(clockInterval)
  if (scheduleRefreshTimer !== undefined) clearInterval(scheduleRefreshTimer)
  document.removeEventListener('visibilitychange', handleScheduleVisibilityChange)
  void hub?.stop()
})
</script>

<template>
  <!-- 启动状态、登录页和已登录主界面互斥显示。 -->
  <main v-if="!apiReady || checkingAuth" class="loading-screen">
    <div v-if="startupFailed" class="loading-failure" role="alert">
      <span class="empty-icon">
        <UiIcon name="alert" :size="22" />
      </span>
      <h2>无法连接服务器</h2>
      <p>请确认 RoomControl 服务已启动、网络可达，然后重试。启动后会自动完成数据库检查。</p>
      <button type="button" class="btn btn-primary" @click="bootstrap">重试连接</button>
    </div>
    <template v-else>
      <span class="spinner"></span>
      <span>{{ apiReady ? '正在连接控制平台…' : '系统启动中…' }}</span>
    </template>
  </main>

  <main v-else-if="!authenticated" class="login-screen">
    <section class="login-shell">
      <div class="login-intro">
        <div class="row">
          <BrandLogo class="login-brand" :logo-data-url="branding.logoDataUrl" :label="branding.displayName" />
          <span class="brand-copy">
            <strong>{{ branding.displayName }}</strong>
            <span>{{ branding.subtitle || 'ROOM CONTROL PLATFORM' }}</span>
          </span>
        </div>

        <ul class="login-features">
          <li class="login-feature">
            <UiIcon name="connection" :size="16" />
            <div><strong>实时状态</strong>中控连接与 Join 回报通过实时通道同步。</div>
          </li>
          <li class="login-feature">
            <UiIcon name="shield" :size="16" />
            <div><strong>受控操作</strong>高风险指令可要求二次确认，角色权限即时生效。</div>
          </li>
          <li class="login-feature">
            <UiIcon name="audit" :size="16" />
            <div><strong>可追溯</strong>每次下发都有会话回执，服务端保留操作记录。</div>
          </li>
        </ul>
      </div>

      <div class="login-card">
        <div>
          <h2>登录平台</h2>
          <p class="panel-sub">使用管理员分配的账号进入。</p>
        </div>
        <form class="login-form" @submit.prevent="login">
          <label class="field">
            用户名
            <select name="username" v-if="loginAccounts.length" v-model="username" class="select" autocomplete="username" required>
              <option value="" disabled>请选择账号</option>
              <option v-for="account in loginAccounts" :key="account.username" :value="account.username">
                {{ account.username }}
              </option>
            </select>
            <input name="username" v-else v-model="username" class="input" maxlength="60" autocomplete="username" required />
          </label>
          <p v-if="selectedLoginRole" class="login-role">
            <strong>{{ selectedLoginRole.label }}</strong>
            <span>{{ selectedLoginRole.description }}</span>
          </p>
          <p v-else class="login-role">
            <strong>提示</strong>
            <span>请联系管理员确认账号权限</span>
          </p>
          <label class="field">
            密码
            <input name="password" v-model="password" class="input" type="password" maxlength="128" autocomplete="current-password" required />
          </label>
          <p v-if="errorMessage" class="login-error" role="alert">{{ errorMessage }}</p>
          <button class="btn btn-primary btn-block" :disabled="busy">
            {{ busy ? '正在登录…' : '登录平台' }}
          </button>
        </form>
        <p class="login-footnote">账号由管理员分配；连续失败会触发登录限制。</p>
      </div>
    </section>
  </main>

  <div v-else class="app-shell" :class="{ 'is-rail-open': railOpen, 'is-rail-collapsed': railCollapsed }">
    <header class="topbar">
      <div class="topbar-brand">
        <button type="button" class="btn btn-ghost btn-icon topbar-menu"
          :aria-label="railOpen ? '关闭任务导航' : '打开任务导航'" :aria-expanded="railOpen"
          @click="railOpen = !railOpen">
          <UiIcon name="menu" :size="18" />
        </button>
        <BrandLogo :logo-data-url="branding.logoDataUrl" :label="branding.displayName" :title="branding.displayName" />
        <span class="brand-copy">
          <strong>{{ branding.displayName }}</strong>
          <span>{{ branding.subtitle }}</span>
        </span>
      </div>

      <div class="topbar-context">
        <strong>{{ activeWorkspaceLabel }}</strong>
      </div>

      <div class="topbar-actions">
        <div class="status-cluster">
          <span class="status-item" :class="`is-${onlineTone}`"
            :title="`${offlineRoomCount} 间离线，${unconfiguredRoomCount} 间未接入`">
            <UiIcon name="connection" :size="13" />
            在线 {{ onlineRoomCount }}/{{ configuredRoomCount }}
          </span>
          <span v-if="canOperate && attentionCount" class="status-item is-warning"
            :title="`${offlineRoomCount} 间离线 · ${unconfiguredRoomCount} 间未接入 · ${overdueScheduleCount} 个逾期任务`">
            <UiIcon name="alert" :size="13" />
            {{ attentionCount }} 项待处理
          </span>
        </div>

        <div v-if="canOperate && rooms.length" class="topbar-search">
          <UiIcon class="search-icon" name="search" :size="14" />
          <input name="room-jump" v-model="roomJumpQuery" class="input" list="room-jump-options" placeholder="跳转教室（回车）" aria-label="跳转教室"
            @keydown.enter.prevent="jumpToRoom" />
          <datalist id="room-jump-options">
            <option v-for="option in roomJumpOptions" :key="option" :value="option"></option>
          </datalist>
        </div>

        <div ref="actionSlot" class="topbar-workspace-slot"></div>

        <div class="topbar-clock">
          <strong>{{ clockTime }}</strong>
          <span>{{ clockDate }}</span>
        </div>
      </div>
    </header>

    <TaskRail :current-user="currentUser" :workspace="workspace" :room-count="rooms.length"
      :online-count="onlineRoomCount" :attention-count="attentionCount"
      :hub-state="hubState" :rail-collapsed="railCollapsed" @navigate="navigate" @logout="logout"
      @toggle-collapse="railCollapsed = !railCollapsed" />

    <button v-if="railOpen" type="button" class="rail-scrim" aria-label="关闭任务导航" @click="railOpen = false"></button>

    <section class="workspace">
      <div ref="workspaceScroll" class="workspace-scroll">
        <div v-if="snapshotError" class="banner is-danger workspace-alert" role="alert">
          <UiIcon name="alert" :size="16" />
          <div>
            <strong>无法读取最新站点数据</strong>
            <p>{{ snapshotError }}。当前显示的数据可能不是最新状态。</p>
          </div>
          <button type="button" class="btn btn-quiet btn-sm" :disabled="busy" @click="refreshData">重试</button>
        </div>

        <OverviewWorkspace v-if="workspace === 'overview'" :rooms="rooms" :schedules="schedules"
          :current-user="currentUser" :now="currentDateTime" @select-room="openRoom"
          @open-automation="openAutomation" @open-settings="openSettings" />

        <OperateWorkspace v-else-if="workspace === 'operate'" :rooms="rooms" :selected-room-id="selectedRoomId"
          :current-user="currentUser" :hub-state="hubState" :last-hub-event-at="lastHubEventAt"
          :debug-enabled="debugEnabled" :receipts="receipts" :schedules="schedules"
          :now="currentDateTime" @select-room="selectedRoomId = $event" @send-command="sendDigitalPulse"
          @schedule-command="scheduleCommand" @open-settings="openSettings"
          @clear-receipts="clearReceipts" />

        <SettingsPage v-else-if="workspace === 'settings'" :rooms="rooms" :branding="branding" :theme="theme"
          :density="density" :section="settingsSection" :current-user="currentUser" :focus-room-id="settingsFocusRoomId"
          :debug-enabled="debugEnabled" :schedules="schedules" :selected-room-id="selectedRoomId"
          :automation-target="automationTarget" :now="currentDateTime"
          @rooms-changed="handleRoomsChanged" @branding-saved="handleBrandingSaved"
          @theme-changed="handleThemeChanged" @density-changed="density = $event"
          @debug-changed="debugEnabled = $event" @notify="handleSettingsNotify"
          @automation-changed="handleAutomationChanged"
          @section-changed="handleSettingsSection" />

        <LogsPage v-else-if="workspace === 'audit'" />
      </div>
    </section>
  </div>

  <div v-if="errorMessage || successMessage" class="toast-stack" aria-live="polite">
    <div class="toast" :class="{ 'is-error': errorMessage, 'is-success': successMessage }" role="status">
      <UiIcon :name="errorMessage ? 'alert' : 'check'" :size="16" />
      <div class="toast-copy">
        <strong>{{ errorMessage ? '操作失败' : '操作完成' }}</strong>
        <span>{{ errorMessage || successMessage }}</span>
      </div>
      <button class="toast-dismiss" aria-label="关闭提示" @click="dismissToast">
        <UiIcon name="x" :size="14" />
      </button>
    </div>
  </div>

  <ConfirmDialog :open="confirmation !== null" :title="confirmation?.title ?? ''" :message="confirmation?.message ?? ''"
    :confirm-label="confirmation?.actionLabel ?? '确认'" :tone="confirmation?.tone ?? 'default'"
    @cancel="resolveConfirmation(false)" @confirm="resolveConfirmation(true)" />
</template>
