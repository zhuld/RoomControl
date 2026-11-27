<script setup lang="ts">
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr'
import { computed, nextTick, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue'
import { readJson, request, toErrorMessage } from './api'
import LoginScreen from './components/LoginScreen.vue'
import LogsPage from './components/LogsPage.vue'
import OperateWorkspace from './components/OperateWorkspace.vue'
import OverviewWorkspace from './components/OverviewWorkspace.vue'
import SettingsPage from './components/SettingsPage.vue'
import TaskRail from './components/TaskRail.vue'
import ConfirmDialog from './components/ui/ConfirmDialog.vue'
import UiIcon from './components/ui/UiIcon.vue'
import { useConfirmDialog } from './lib/confirm'
import { actionSlotKey } from './lib/layout'
import {
  canAccessSettingsSection,
  defaultSettingsSection,
} from './lib/settings'
import BrandLogo from './components/BrandLogo.vue'
import {
  defaultBranding,
  resolveThemeName,
  workspaceMeta,
  type AutomationTarget,
  type Branding,
  type CommandReceipt,
  type CurrentUser,
  type Density,
  type JoinMapping,
  type ManualTask,
  type Room,
  type Schedule,
  type SettingsSection,
  type ThemeName,
  type UserRole,
  type Workspace,
} from './types'
import CanvasNest from './components/ui/CanvasNest.vue'

// 根组件维护会话、连接、当前工作区与命令回执；页面只负责各自任务流。
const authenticated = ref(false)
const checkingAuth = ref(true)
const apiReady = ref(false)
const startupFailed = ref(false)
const busy = ref(false)
const rooms = ref<Room[]>([])
const schedules = ref<Schedule[]>([])
const manualTasks = ref<ManualTask[]>([])
const selectedRoomId = ref('')
const errorMessage = ref('')
const successMessage = ref('')
const snapshotError = ref('')
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

const activeWorkspace = computed(() => workspaceMeta[workspace.value])

const theme = ref<ThemeName>('pine')
const density = ref<Density>('comfortable')
// 各工作区的页头操作统一传送到顶部栏。
const actionSlot = ref<HTMLElement | null>(null)
provide(actionSlotKey, actionSlot)
const currentDateTime = ref(new Date())
let clockInterval: ReturnType<typeof setInterval> | undefined
let toastTimeout: ReturnType<typeof setTimeout> | undefined
// 自动任务在后台执行后没有推送事件；轮询计划数据以清除过期的逾期提示。
const SCHEDULE_REFRESH_MS = 15_000
let scheduleRefreshTimer: ReturnType<typeof setInterval> | undefined

const hubState = ref<'connecting' | 'connected' | 'disconnected'>('disconnected')
const lastHubEventAt = ref<string | null>(null)
let hub: HubConnection | null = null
// SignalR 自动重连放弃后由这里继续重试；令牌让旧连接的回调不再影响当前状态。
const HUB_RETRY_MS = 5_000
let hubRetryTimer: ReturnType<typeof setTimeout> | undefined
let hubConnectToken = 0

const receipts = ref<CommandReceipt[]>([])
const RECEIPT_STORAGE_KEY = 'roomcontrol.receipts'

const { confirmation, confirmAction, resolveConfirmation } = useConfirmDialog()

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

function handleManualTasksChanged() {
  void loadManualTasks()
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
  rooms.value = await readJson<Room[]>('/api/rooms')
  ensureSelectedRoom()
}

function ensureSelectedRoom() {
  if (rooms.value.some((room) => room.id === selectedRoomId.value)) return
  const storedRoom = localStorage.getItem('roomcontrol.room') ?? ''
  selectedRoomId.value =
    rooms.value.find((room) => room.id === storedRoom)?.id ?? rooms.value[0]?.id ?? ''
}

// 实时推送只覆盖变化：握手、重连或断流期间的推送会丢失，
// 因此按接口快照校准中控在线状态与控制点状态，保证界面最终与数据库一致。
async function reconcileRooms() {
  if (!authenticated.value) return
  let latest: Room[]
  try {
    latest = await readJson<Room[]>('/api/rooms')
  } catch {
    // 校准失败时保留现有数据，等下一次推送或校准。
    return
  }

  // 教室被增删时整体替换，让订阅的教室分组随之更新；否则只合并状态字段。
  if (
    latest.length !== rooms.value.length ||
    latest.some((room) => !rooms.value.some((item) => item.id === room.id))
  ) {
    rooms.value = latest
    ensureSelectedRoom()
    return
  }

  for (const room of rooms.value) {
    const controller = room.controller
    const latestController = latest.find((item) => item.id === room.id)?.controller
    if (!controller || !latestController) continue
    controller.isOnline = latestController.isOnline
    controller.lastError = latestController.lastError
    const latestJoins = new Map(latestController.joins.map((join) => [join.id, join]))
    for (const join of controller.joins) {
      const latestJoin = latestJoins.get(join.id)
      if (!latestJoin) continue
      join.stateSnapshot = latestJoin.stateSnapshot
      join.enableSnapshot = latestJoin.enableSnapshot
    }
  }
}

async function loadSchedules() {
  schedules.value = await readJson<Schedule[]>('/api/schedules')
}

async function loadManualTasks() {
  manualTasks.value = await readJson<ManualTask[]>('/api/manual-tasks')
}

// 计划任务与手动任务由后台或他人修改时前端没有事件；定期刷新以同步任务按钮与逾期状态。
// 空闲时额外校准中控状态：断线期间没有推送，靠快照保持界面与实际连接一致。
async function refreshSchedulesInBackground() {
  if (!authenticated.value || document.hidden) return
  const tasks: Promise<void>[] = [loadSchedules(), loadManualTasks()]
  if (hubState.value !== 'connected') tasks.push(reconcileRooms())
  try {
    await Promise.all(tasks)
  } catch {
    // 后台刷新失败时保留现有数据，下一次轮询继续重试。
  }
}

function handleScheduleVisibilityChange() {
  if (document.hidden) return
  void refreshSchedulesInBackground()
  // 页面长时间隐藏时推送可能已中断，回到前台先校准一次。
  void reconcileRooms()
}

async function loadCurrentUser() {
  try {
    const me = await readJson<{ username: string; role: UserRole }>('/api/auth/me')
    currentUser.value = { username: me.username, role: me.role }
  } catch {
    currentUser.value = null
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
    checkingAuth.value = false
  }
}

async function loadSnapshot() {
  try {
    await Promise.all([loadRooms(), loadSchedules(), loadManualTasks()])
    snapshotError.value = ''
  } catch (error) {
    snapshotError.value = toErrorMessage(error)
    throw error
  }
}

// 登录页完成凭据校验后把身份交回根组件，由这里加载站点数据并进入主界面。
async function handleAuthenticated(user: CurrentUser) {
  currentUser.value = user
  authenticated.value = true
  dismissToast()
  try {
    await loadSnapshot()
  } catch (error) {
    showError(error)
  }
  clearReceipts()
  workspace.value = 'overview'
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
    hubConnectToken += 1
    clearHubRetry()
    await hub?.stop()
    hub = null
    hubState.value = 'disconnected'
    lastHubEventAt.value = null
    authenticated.value = false
    currentUser.value = null
    rooms.value = []
    schedules.value = []
    manualTasks.value = []
    snapshotError.value = ''
    workspace.value = 'overview'
    clearReceipts()
    dismissToast()
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

// 导入会同时替换站点品牌与全部位置、教室和任务，需要刷新整个快照。
async function handleConfigImported() {
  await loadBranding()
  await handleRoomsChanged()
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

function clearHubRetry() {
  if (hubRetryTimer !== undefined) {
    clearTimeout(hubRetryTimer)
    hubRetryTimer = undefined
  }
}

// 自动重连在若干次尝试后会放弃；这里继续按固定间隔重试，避免停机或休眠后一直收不到推送。
function scheduleHubReconnect() {
  if (hubRetryTimer !== undefined || !authenticated.value) return
  hubRetryTimer = setTimeout(() => {
    hubRetryTimer = undefined
    if (authenticated.value) void connectToRoomHub(rooms.value.map((room) => room.id))
  }, HUB_RETRY_MS)
}

async function connectToRoomHub(roomIds: string[]) {
  const token = (hubConnectToken += 1)
  clearHubRetry()
  const previous = hub
  hub = null
  await previous?.stop()
  if (!roomIds.length || !authenticated.value) {
    hubState.value = 'disconnected'
    return
  }

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
  connection.onreconnecting(() => {
    if (token === hubConnectToken) hubState.value = 'connecting'
  })
  connection.onreconnected(async () => {
    if (token !== hubConnectToken) return
    hubState.value = 'connected'
    try {
      await Promise.all(roomIds.map((roomId) => connection.invoke('JoinRoom', roomId)))
    } catch {
      // 重新加入分组失败时按快照兜底，状态仍可校准。
    }
    // 断线期间的变化不会补发，重连并重新加入分组后按快照校准。
    await reconcileRooms()
  })
  connection.onclose(() => {
    if (token !== hubConnectToken) return
    hubState.value = 'disconnected'
    scheduleHubReconnect()
  })

  try {
    await connection.start()
    if (token !== hubConnectToken) {
      await connection.stop()
      return
    }
    await Promise.all(roomIds.map((roomId) => connection.invoke('JoinRoom', roomId)))
    hub = connection
    hubState.value = 'connected'
    // 从读取快照到加入分组之间的推送已经错过，先用最新快照补齐。
    await reconcileRooms()
  } catch {
    if (token === hubConnectToken) hubState.value = 'disconnected'
    await connection.stop().catch(() => undefined)
    scheduleHubReconnect()
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
  clearHubRetry()
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
      <CanvasNest />
      <span class="spinner"></span>
      <span>{{ apiReady ? '正在连接控制平台…' : '系统启动中…' }}</span>
    </template>
  </main>

  <LoginScreen v-else-if="!authenticated" :branding="branding" @authenticated="handleAuthenticated" />

  <div v-else class="app-shell" :class="{ 'is-rail-open': railOpen, 'is-rail-collapsed': railCollapsed }">
    <header class="topbar">
      <div class="topbar-brand">
        <button type="button" class="btn btn-ghost btn-icon topbar-menu" :aria-label="railOpen ? '关闭任务导航' : '打开任务导航'"
          :aria-expanded="railOpen" @click="railOpen = !railOpen">
          <UiIcon name="menu" :size="18" />
        </button>
        <BrandLogo :logo-data-url="branding.logoDataUrl" :label="branding.displayName" :title="branding.displayName" />
        <span class="brand-copy">
          <strong>{{ branding.displayName }}</strong>
        </span>
      </div>
      <UiIcon class="rail-icon" :name="activeWorkspace.icon" />
      <div class="topbar-context">
        <strong>{{ activeWorkspace.label }}</strong>
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
          <input name="room-jump" v-model="roomJumpQuery" class="input" list="room-jump-options" placeholder="跳转教室（回车）"
            aria-label="跳转教室" @keydown.enter.prevent="jumpToRoom" />
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
      :online-count="onlineRoomCount" :attention-count="attentionCount" :hub-state="hubState"
      :rail-collapsed="railCollapsed" @navigate="navigate" @logout="logout"
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
          :manual-tasks="manualTasks" :current-user="currentUser" :now="currentDateTime" @select-room="openRoom"
          @open-automation="openAutomation" @open-settings="openSettings" @notify="handleSettingsNotify" />

        <OperateWorkspace v-else-if="workspace === 'operate'" :rooms="rooms" :selected-room-id="selectedRoomId"
          :current-user="currentUser" :hub-state="hubState" :last-hub-event-at="lastHubEventAt" :receipts="receipts"
          :schedules="schedules" :now="currentDateTime" @select-room="selectedRoomId = $event"
          @send-command="sendDigitalPulse" @schedule-command="scheduleCommand" @open-settings="openSettings"
          @clear-receipts="clearReceipts" />

        <SettingsPage v-else-if="workspace === 'settings'" :rooms="rooms" :branding="branding" :theme="theme"
          :density="density" :section="settingsSection" :current-user="currentUser" :focus-room-id="settingsFocusRoomId"
          :schedules="schedules" :manual-tasks="manualTasks" :selected-room-id="selectedRoomId"
          :automation-target="automationTarget" :now="currentDateTime" @rooms-changed="handleRoomsChanged"
          @branding-saved="handleBrandingSaved" @theme-changed="handleThemeChanged" @density-changed="density = $event"
          @notify="handleSettingsNotify" @automation-changed="handleAutomationChanged"
          @manual-tasks-changed="handleManualTasksChanged" @section-changed="handleSettingsSection"
          @config-imported="handleConfigImported" />

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
