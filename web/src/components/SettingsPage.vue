<script setup lang="ts">
import { computed, inject, reactive, ref, watch } from 'vue'
import { readJson, request, toErrorMessage } from '../api'
import { useConfirmDialog } from '../lib/confirm'
import { actionSlotKey } from '../lib/layout'
import { canAccessSettingsSection } from '../lib/settings'
import {
  buildingOf,
  floorOf,
  groupRoomsByBuilding,
  roomStatusLabel,
  roomStatusTone,
  sortRoomsByLocation,
} from '../lib/rooms'
import {
  densityOptions,
  settingsSectionMeta,
  settingsSections,
  themeOptions,
  type AutomationTarget,
  type Branding,
  type BuildingOption,
  type ControllerTypeOption,
  type CurrentUser,
  type Density,
  type FeedbackTone,
  type FloorOption,
  type JoinMapping,
  type ManualTask,
  type Room,
  type Schedule,
  type SettingsSection,
  type ThemeName,
} from '../types'
import AutomationWorkspace from './AutomationWorkspace.vue'
import BrandLogo from './BrandLogo.vue'
import ManualTasksWorkspace from './ManualTasksWorkspace.vue'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import EmptyState from './ui/EmptyState.vue'
import RoomCascader from './ui/RoomCascader.vue'
import RoomTree from './ui/RoomTree.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'
import UsersPage from './UsersPage.vue'

// 配置页按接入顺序组织：站点 → 位置 → 教室 → 中控 → 自动任务 → 手动任务 → 权限。
const props = defineProps<{
  rooms: Room[]
  branding: Branding
  theme: ThemeName
  density: Density
  section: SettingsSection
  currentUser: CurrentUser | null
  focusRoomId?: string
  schedules: Schedule[]
  manualTasks: ManualTask[]
  selectedRoomId: string
  automationTarget: AutomationTarget | null
  now: Date
}>()

const emit = defineEmits<{
  roomsChanged: []
  brandingSaved: [branding: Branding]
  notify: [payload: { message: string; isError: boolean }]
  themeChanged: [theme: ThemeName]
  densityChanged: [density: Density]
  sectionChanged: [section: SettingsSection]
  automationChanged: []
  manualTasksChanged: []
}>()

interface SetupStep {
  value: SettingsSection
  title: string
  summary: string
  done: boolean
}

interface HealthItem {
  id: string
  tone: FeedbackTone
  text: string
  actionLabel: string
  section: SettingsSection
  roomId?: string
}

const canManageSettings = computed(() => props.currentUser?.role === 'admin')
const actionSlot = inject(actionSlotKey, ref<HTMLElement | null>(null))
const busy = ref(false)
const setupLoading = ref(false)
const setupLoadError = ref('')
const { confirmation, confirmAction, resolveConfirmation } = useConfirmDialog({
  actionLabel: '确认删除',
  tone: 'danger',
})

// Logo 限制需与后端校验及存储上限保持一致。
const maxLogoBytes = 200 * 1024

const brandingForm = reactive({
  displayName: props.branding.displayName,
  subtitle: props.branding.subtitle,
  logoDataUrl: props.branding.logoDataUrl as string | null,
})

// 日志保留天数与后端校验保持一致；0 表示永久保留。
const MAX_LOG_RETENTION_DAYS = 3650
const logRetentionDays = ref<number | null>(null)
const logRetentionInput = ref<number>(60)
const logRetentionError = ref('')

const buildings = ref<BuildingOption[]>([])
const controllerTypes = ref<ControllerTypeOption[]>([])
const buildingForm = reactive({ name: '' })
const editingBuildingId = ref('')
const buildingEditName = ref('')
const floorDrafts = reactive<Record<string, string>>({})
const editingFloorId = ref('')
const floorEditName = ref('')

const roomForm = reactive({ name: '', building: '', floor: '' })
const editingRoomId = ref('')
const roomEdit = reactive({ name: '', building: '', floor: '' })

const configuredRoomId = ref('')
const controllerForm = reactive({
  adapterType: 'crestron-cip',
  host: '127.0.0.1',
  port: 41794,
  ipid: 3,
  enabled: true,
})

// 添加与编辑共用同一份表单状态，joinDialogTargetId 为空表示新建。
const joinForm = reactive({
  name: '',
  sendJoinNumber: 1,
  stateJoinNumber: 2,
  enableJoinNumber: 0 as number | null,
  showConfirmationPopup: true,
})
const joinDialogOpen = ref(false)
const joinDialogTargetId = ref('')
const copyingJoin = ref<JoinMapping | null>(null)
const copyingAll = ref(false)
const copyTargetControllerIds = ref<string[]>([])

const sortedRooms = computed(() => sortRoomsByLocation(props.rooms))

const totalFloorCount = computed(() =>
  buildings.value.reduce((sum, building) => sum + building.floors.length, 0),
)

const configuredRoomCount = computed(() => props.rooms.filter((room) => room.controller).length)
const joinCount = computed(() =>
  props.rooms.reduce((sum, room) => sum + (room.controller?.joins.length ?? 0), 0),
)
const roomsWithoutController = computed(() => props.rooms.filter((room) => !room.controller))
const controllersWithoutJoins = computed(() =>
  props.rooms.filter((room) => room.controller && room.controller.joins.length === 0),
)

// 同一任务可覆盖多间教室，接口会为每间教室返回一个目标；按 GroupId 合并后计数。
const activeAutomationCount = computed(() => {
  const groups = new Set<string>()
  for (const schedule of props.schedules) {
    if (schedule.enabled) groups.add(schedule.groupId ?? schedule.id)
  }
  return groups.size
})

const currentThemeLabel = computed(
  () => themeOptions.find((option) => option.value === props.theme)?.label ?? props.theme,
)

// 步骤顺序与标题取自 settingsSections，这里只补充随数据变化的进度；缺键会由类型检查发现。
const setupSteps = computed<SetupStep[]>(() => {
  const progress: Record<SettingsSection, Pick<SetupStep, 'summary' | 'done'>> = {
    branding: {
      summary: props.branding.displayName || '未命名站点',
      done: Boolean(props.branding.displayName.trim()),
    },
    locations: {
      summary: `${buildings.value.length} 栋 · ${totalFloorCount.value} 层`,
      done: buildings.value.length > 0 && totalFloorCount.value > 0,
    },
    rooms: {
      summary: `${props.rooms.length} 间`,
      done: props.rooms.length > 0,
    },
    controllers: {
      summary: `${configuredRoomCount.value}/${props.rooms.length} 接入 · ${joinCount.value} 个控制点`,
      done: props.rooms.length > 0 && roomsWithoutController.value.length === 0,
    },
    automation: {
      summary: activeAutomationCount.value ? `${activeAutomationCount.value} 个启用任务` : '暂无启用任务',
      done: activeAutomationCount.value > 0,
    },
    'manual-tasks': {
      summary: props.manualTasks.length ? `${props.manualTasks.length} 个手动任务` : '暂无手动任务',
      done: props.manualTasks.length > 0,
    },
    users: {
      summary: '管理员 / 操作员 / 观看员',
      done: true,
    },
    appearance: {
      summary: `主题：${currentThemeLabel.value}`,
      done: currentThemeLabel.value !== null && currentThemeLabel.value !== undefined,
    },
  }

  return settingsSections.map((section) => ({
    value: section.value,
    title: section.title,
    ...progress[section.value],
  }))
})

const visibleSteps = computed(() =>
  setupSteps.value.filter((step) => canAccessSettingsSection(step.value, props.currentUser?.role)),
)

// 当前步骤若不在角色可访问范围内，回退到第一个可见步骤，避免越权渲染配置内容。
const activeSection = computed<SettingsSection>(() =>
  visibleSteps.value.some((step) => step.value === props.section)
    ? props.section
    : (visibleSteps.value[0]?.value ?? 'appearance'),
)

const meta = computed(() => settingsSectionMeta[activeSection.value])

const healthItems = computed<HealthItem[]>(() => {
  const items: HealthItem[] = []
  if (!buildings.value.length) {
    items.push({
      id: 'no-buildings',
      tone: 'warning',
      text: '尚未创建楼栋，教室无法选择位置。',
      actionLabel: '去添加',
      section: 'locations',
    })
  }
  if (!props.rooms.length) {
    items.push({
      id: 'no-rooms',
      tone: 'warning',
      text: '尚未创建教室，控制台没有可操作的房间。',
      actionLabel: '去添加',
      section: 'rooms',
    })
  }
  if (roomsWithoutController.value.length) {
    items.push({
      id: 'missing-controllers',
      tone: 'danger',
      text: `${roomsWithoutController.value.length} 间教室尚未接入中控。`,
      actionLabel: '去接入',
      section: 'controllers',
      roomId: roomsWithoutController.value[0]?.id,
    })
  }
  if (controllersWithoutJoins.value.length) {
    items.push({
      id: 'missing-joins',
      tone: 'warning',
      text: `${controllersWithoutJoins.value.length} 个中控还没有控制点，无法下发指令。`,
      actionLabel: '去配置',
      section: 'controllers',
      roomId: controllersWithoutJoins.value[0]?.id,
    })
  }
  return items
})

const healthOk = computed(() => healthItems.value.length === 0)

// 中控页使用楼栋 → 楼层 → 教室三级选择，并保留列表快速浏览。
const controllerGroups = computed(() => groupRoomsByBuilding(sortedRooms.value))

const configuredRoom = computed(
  () => props.rooms.find((room) => room.id === configuredRoomId.value) ?? null,
)

const createFloorOptions = computed(() => floorOptionsFor(roomForm.building))
const editFloorOptions = computed(() => floorOptionsFor(roomEdit.building))

const copyTargetRooms = computed(() =>
  sortedRooms.value.filter(
    (room) => room.controller && room.controller.id !== configuredRoom.value?.controller?.id,
  ),
)

watch(
  () => props.branding,
  (value) => {
    brandingForm.displayName = value.displayName
    brandingForm.subtitle = value.subtitle
    brandingForm.logoDataUrl = value.logoDataUrl
  },
  { deep: true },
)

watch(
  () => props.focusRoomId,
  (roomId) => {
    if (roomId && props.rooms.some((room) => room.id === roomId)) {
      configuredRoomId.value = roomId
    }
  },
  { immediate: true },
)

watch(
  () => props.rooms,
  (rooms) => {
    if (!rooms.some((room) => room.id === configuredRoomId.value)) {
      configuredRoomId.value = rooms[0]?.id ?? ''
    }
  },
  { immediate: true },
)

watch(configuredRoomId, fillControllerForm, { immediate: true })
watch(
  () => roomForm.building,
  () => ensureFormFloor(roomForm, createFloorOptions.value),
)
watch(
  () => roomEdit.building,
  () => ensureFormFloor(roomEdit, editFloorOptions.value),
)

// 控制点顺序以接口返回的 SortOrder 为准，拖拽时先更新本地列表再保存。
const joinRows = ref<JoinMapping[]>([])
const draggingJoinId = ref('')
const dragOverJoinId = ref('')

watch(
  () => configuredRoom.value?.controller?.joins,
  (joins) => {
    joinRows.value = joins ? [...joins] : []
  },
  { immediate: true },
)

async function loadSetupData() {
  setupLoading.value = true
  setupLoadError.value = ''
  try {
    await Promise.all([loadBuildings(), loadControllerTypes()])
  } catch (error) {
    setupLoadError.value = toErrorMessage(error)
  } finally {
    setupLoading.value = false
  }
}

// 楼栋、中控类型与日志设置接口仅管理员可读，操作员只维护自动任务时无需拉取。
watch(
  canManageSettings,
  (allowed) => {
    if (allowed) {
      void loadSetupData()
      void loadLogRetention()
    }
  },
  { immediate: true },
)

function floorOptionsFor(buildingName: string): FloorOption[] {
  return buildings.value.find((building) => building.name === buildingName)?.floors ?? []
}

function ensureFormFloor(form: { building: string; floor: string }, options: FloorOption[]) {
  // 楼栋变化后保留有效楼层，否则自动选择新楼栋的首个可用楼层。
  if (!options.some((floor) => floor.name === form.floor)) {
    form.floor = options[0]?.name ?? ''
  }
}

async function loadBuildings() {
  buildings.value = await readJson<BuildingOption[]>('/api/buildings')
  if (!buildings.value.some((building) => building.name === roomForm.building)) {
    roomForm.building = buildings.value[0]?.name ?? ''
  }
}

async function loadControllerTypes() {
  controllerTypes.value = await readJson<ControllerTypeOption[]>('/api/controller-types')
}

function fillControllerForm() {
  const controller = configuredRoom.value?.controller
  controllerForm.adapterType = controller?.adapterType ?? controllerTypes.value[0]?.key ?? 'crestron-cip'
  controllerForm.host = controller?.host ?? '127.0.0.1'
  controllerForm.port = controller?.port ?? 41794
  controllerForm.ipid = controller?.ipid ?? 3
  controllerForm.enabled = controller?.enabled ?? true
}

function jumpTo(section: SettingsSection, roomId?: string) {
  if (roomId && props.rooms.some((room) => room.id === roomId)) {
    configuredRoomId.value = roomId
  }
  emit('sectionChanged', section)
}

async function run(action: () => Promise<void>, successMessage: string) {
  busy.value = true
  try {
    await action()
    emit('notify', { message: successMessage, isError: false })
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    busy.value = false
  }
}

function onLogoSelected(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (!file.type.startsWith('image/')) {
    emit('notify', { message: '请选择 PNG、JPG、SVG 或 WebP 图片。', isError: true })
    return
  }
  if (file.size > maxLogoBytes) {
    emit('notify', { message: 'Logo 文件不能超过 200 KB。', isError: true })
    return
  }

  const reader = new FileReader()
  reader.onload = () => {
    brandingForm.logoDataUrl = typeof reader.result === 'string' ? reader.result : null
  }
  reader.onerror = () => emit('notify', { message: '读取图片失败，请重试。', isError: true })
  reader.readAsDataURL(file)
}

async function saveBranding() {
  await run(async () => {
    const saved = await request('/api/branding', {
      method: 'PUT',
      body: JSON.stringify(brandingForm),
    }).then((response) => response.json() as Promise<Branding>)
    emit('brandingSaved', saved)
  }, '站点信息已保存')
}

async function loadLogRetention() {
  try {
    const settings = await readJson<{ retentionDays: number }>('/api/logs/settings')
    logRetentionDays.value = settings.retentionDays
    logRetentionInput.value = settings.retentionDays
    logRetentionError.value = ''
  } catch (error) {
    logRetentionError.value = toErrorMessage(error)
  }
}

async function saveLogRetention() {
  const days = logRetentionInput.value
  if (!Number.isInteger(days) || days < 0 || days > MAX_LOG_RETENTION_DAYS) {
    logRetentionError.value = `保留天数需为 0–${MAX_LOG_RETENTION_DAYS} 之间的整数，0 表示永久保留。`
    return
  }

  logRetentionError.value = ''
  await run(async () => {
    const saved = await request('/api/logs/settings', {
      method: 'PUT',
      body: JSON.stringify({ retentionDays: days }),
    }).then((response) => response.json() as Promise<{ retentionDays: number }>)
    logRetentionDays.value = saved.retentionDays
    logRetentionInput.value = saved.retentionDays
  }, days === 0 ? '已保存：永久保留日志' : `已保存：仅保留最近 ${days} 天的记录`)
}

function selectTheme(value: ThemeName) {
  emit('themeChanged', value)
}

function selectDensity(value: Density) {
  emit('densityChanged', value)
}

async function createBuilding() {
  const name = buildingForm.name.trim()
  if (!name) return
  await run(async () => {
    await request('/api/buildings', { method: 'POST', body: JSON.stringify({ name }) })
    buildingForm.name = ''
    await loadBuildings()
  }, `楼栋「${name}」已添加`)
}

function startEditBuilding(building: BuildingOption) {
  editingBuildingId.value = building.id
  buildingEditName.value = building.name
}

async function saveBuilding(building: BuildingOption) {
  await run(async () => {
    await request(`/api/buildings/${building.id}`, {
      method: 'PUT',
      body: JSON.stringify({ name: buildingEditName.value }),
    })
    editingBuildingId.value = ''
    await loadBuildings()
    emit('roomsChanged')
  }, `楼栋「${building.name}」已重命名`)
}

async function deleteBuilding(building: BuildingOption) {
  if (!(await confirmAction(`删除楼栋「${building.name}」及其 ${building.floors.length} 个楼层？`))) return
  await run(async () => {
    await request(`/api/buildings/${building.id}`, { method: 'DELETE' })
    await loadBuildings()
    emit('roomsChanged')
  }, `楼栋「${building.name}」已删除`)
}

async function createFloor(building: BuildingOption) {
  const name = (floorDrafts[building.id] ?? '').trim()
  if (!name) return
  await run(async () => {
    await request(`/api/buildings/${building.id}/floors`, {
      method: 'POST',
      body: JSON.stringify({ name }),
    })
    floorDrafts[building.id] = ''
    await loadBuildings()
  }, `楼层「${name}」已添加`)
}

function startEditFloor(floor: FloorOption) {
  editingFloorId.value = floor.id
  floorEditName.value = floor.name
}

async function saveFloor(floor: FloorOption) {
  await run(async () => {
    await request(`/api/floors/${floor.id}`, {
      method: 'PUT',
      body: JSON.stringify({ name: floorEditName.value }),
    })
    editingFloorId.value = ''
    await loadBuildings()
    emit('roomsChanged')
  }, `楼层「${floor.name}」已重命名`)
}

async function deleteFloor(building: BuildingOption, floor: FloorOption) {
  if (!(await confirmAction(`删除「${building.name} · ${floor.name}」？`))) return
  await run(async () => {
    await request(`/api/floors/${floor.id}`, { method: 'DELETE' })
    await loadBuildings()
  }, `楼层「${floor.name}」已删除`)
}

async function createRoom() {
  const name = roomForm.name.trim()
  if (!name) return
  await run(async () => {
    await request('/api/rooms', { method: 'POST', body: JSON.stringify(roomForm) })
    roomForm.name = ''
    emit('roomsChanged')
  }, `教室「${name}」已创建`)
}

function startEditRoom(room: Room) {
  editingRoomId.value = room.id
  roomEdit.name = room.name
  roomEdit.building = room.building
  roomEdit.floor = room.floor
}

async function saveRoom(room: Room) {
  await run(async () => {
    await request(`/api/rooms/${room.id}`, { method: 'PUT', body: JSON.stringify(roomEdit) })
    editingRoomId.value = ''
    emit('roomsChanged')
  }, `教室「${room.name}」信息已更新`)
}

async function deleteRoom(room: Room) {
  if (!(await confirmAction(`删除教室「${room.name}」及其中控、控制点？关联的自动任务会一并删除。`))) return
  await run(async () => {
    await request(`/api/rooms/${room.id}`, { method: 'DELETE' })
    if (configuredRoomId.value === room.id) configuredRoomId.value = ''
    emit('roomsChanged')
  }, `教室「${room.name}」已删除`)
}

async function saveController() {
  const room = configuredRoom.value
  if (!room) return
  const controller = room.controller
  await run(async () => {
    // 已有连接使用 PUT 更新；首次接入则通过房间子资源创建。
    if (controller) {
      await request(`/api/controllers/${controller.id}`, {
        method: 'PUT',
        body: JSON.stringify(controllerForm),
      })
    } else {
      await request(`/api/rooms/${room.id}/controller`, {
        method: 'POST',
        body: JSON.stringify({
          adapterType: controllerForm.adapterType,
          host: controllerForm.host,
          port: controllerForm.port,
          ipid: controllerForm.ipid,
        }),
      })
    }
    emit('roomsChanged')
  }, controller ? `中控「${controller.name}」配置已更新` : '中控接入配置已保存，后台正在连接')
}

async function deleteController() {
  const controller = configuredRoom.value?.controller
  if (!controller) return
  if (!(await confirmAction(`移除中控「${controller.name}」及其控制点？关联的自动任务会一并删除。`))) return
  await run(async () => {
    await request(`/api/controllers/${controller.id}`, { method: 'DELETE' })
    emit('roomsChanged')
  }, `中控「${controller.name}」已移除`)
}

function openCreateJoin() {
  joinDialogTargetId.value = ''
  joinForm.name = ''
  joinForm.sendJoinNumber = 1
  joinForm.stateJoinNumber = 2
  joinForm.enableJoinNumber = 0
  joinForm.showConfirmationPopup = true
  joinDialogOpen.value = true
}

function openEditJoin(join: JoinMapping) {
  joinDialogTargetId.value = join.id
  joinForm.name = join.name
  joinForm.sendJoinNumber = join.sendJoinNumber
  joinForm.stateJoinNumber = join.stateJoinNumber
  joinForm.enableJoinNumber = join.enableJoinNumber
  joinForm.showConfirmationPopup = join.showConfirmationPopup
  joinDialogOpen.value = true
}

function closeJoinDialog() {
  joinDialogOpen.value = false
  joinDialogTargetId.value = ''
}

async function submitJoin() {
  const controller = configuredRoom.value?.controller
  if (!controller) return
  const name = joinForm.name.trim()
  if (
    !name ||
    !isValidJoinNumber(joinForm.sendJoinNumber) ||
    !isValidJoinNumber(joinForm.stateJoinNumber) ||
    !isValidOptionalJoinNumber(joinForm.enableJoinNumber)
  ) {
    emit('notify', {
      message: '请填写控制点名称，并确保发送、状态 Join 在 1 到 32768 之间；使能 Join 可为 0 或 1 到 32768。',
      isError: true,
    })
    return
  }

  const targetId = joinDialogTargetId.value
  const payload = {
    name,
    sendJoinNumber: joinForm.sendJoinNumber,
    stateJoinNumber: joinForm.stateJoinNumber,
    enableJoinNumber: Number(joinForm.enableJoinNumber) || 0,
    showConfirmationPopup: joinForm.showConfirmationPopup,
  }
  await run(async () => {
    if (targetId) {
      await request(`/api/joins/${targetId}`, { method: 'PUT', body: JSON.stringify(payload) })
    } else {
      await request(`/api/controllers/${controller.id}/joins`, { method: 'POST', body: JSON.stringify(payload) })
    }
    closeJoinDialog()
    emit('roomsChanged')
  }, targetId ? `控制点「${name}」已更新` : `控制点「${name}」已添加`)
}

async function deleteJoin(join: JoinMapping) {
  if (!(await confirmAction(`删除控制点「${join.name}」？关联的自动任务会一并删除。`))) return
  await run(async () => {
    await request(`/api/joins/${join.id}`, { method: 'DELETE' })
    emit('roomsChanged')
  }, `控制点「${join.name}」已删除`)
}

function startJoinDrag(event: DragEvent, join: JoinMapping) {
  draggingJoinId.value = join.id
  dragOverJoinId.value = ''
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', join.id)
  }
}

function dragJoinOver(event: DragEvent, join: JoinMapping) {
  if (!draggingJoinId.value || draggingJoinId.value === join.id) return
  event.preventDefault()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
  dragOverJoinId.value = join.id
}

function clearJoinDragOver(join: JoinMapping) {
  if (dragOverJoinId.value === join.id) dragOverJoinId.value = ''
}

function endJoinDrag() {
  draggingJoinId.value = ''
  dragOverJoinId.value = ''
}

function dropJoin(event: DragEvent, target: JoinMapping) {
  event.preventDefault()
  const sourceId = draggingJoinId.value
  endJoinDrag()
  if (!sourceId || sourceId === target.id) return

  const previous = [...joinRows.value]
  const rows = [...previous]
  const from = rows.findIndex((join) => join.id === sourceId)
  const to = rows.findIndex((join) => join.id === target.id)
  if (from < 0 || to < 0) return

  rows.splice(to, 0, rows.splice(from, 1)[0])
  joinRows.value = rows
  void saveJoinOrder(previous)
}

async function saveJoinOrder(previous: JoinMapping[]) {
  const controller = configuredRoom.value?.controller
  if (!controller) return
  busy.value = true
  try {
    await request(`/api/controllers/${controller.id}/joins/order`, {
      method: 'PUT',
      body: JSON.stringify({ mappingIds: joinRows.value.map((join) => join.id) }),
    })
    emit('roomsChanged')
    emit('notify', { message: '控制点顺序已更新', isError: false })
  } catch (error) {
    // 保存失败时恢复拖拽前的顺序，避免界面与服务端不一致。
    joinRows.value = previous
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    busy.value = false
  }
}

function openCopyJoin(join: JoinMapping) {
  copyingJoin.value = join
  copyingAll.value = false
  copyTargetControllerIds.value = []
}

function openCopyAll() {
  copyingJoin.value = null
  copyingAll.value = true
  copyTargetControllerIds.value = []
}

function selectAllCopyTargets() {
  copyTargetControllerIds.value = copyTargetRooms.value
    .map((room) => room.controller?.id)
    .filter((id): id is string => Boolean(id))
}

function clearCopyTargets() {
  copyTargetControllerIds.value = []
}

function closeCopyJoin() {
  copyingJoin.value = null
  copyingAll.value = false
  copyTargetControllerIds.value = []
}

async function copyJoin() {
  if (copyTargetControllerIds.value.length === 0) return
  if (!copyingJoin.value && !copyingAll.value) return
  const joinName = copyingJoin.value?.name
  const controller = configuredRoom.value?.controller
  if (!controller) return
  const isCopyAll = copyingAll.value
  busy.value = true
  try {
    const path = copyingJoin.value
      ? `/api/joins/${copyingJoin.value.id}/copy`
      : `/api/controllers/${controller.id}/joins/copy-all`
    const response = await request(path, {
      method: 'POST',
      body: JSON.stringify({ controllerIds: copyTargetControllerIds.value }),
    })
    const result = (await response.json()) as { copiedCount: number; controlPointCount?: number }
    closeCopyJoin()
    emit('roomsChanged')
    if (isCopyAll) {
      emit('notify', {
        message: `已向 ${result.copiedCount} 个教室复制全部 ${result.controlPointCount ?? 0} 个控制点`,
        isError: false,
      })
    } else {
      emit('notify', {
        message: `控制点“${joinName}”已复制到 ${result.copiedCount} 个教室`,
        isError: false,
      })
    }
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    busy.value = false
  }
}

function isValidJoinNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= 32768
}

// 0/空值表示不配置使能 Join，其他有效值须与发送、状态 Join 的范围一致。
function isValidOptionalJoinNumber(value: unknown): boolean {
  return value === null || value === '' || value === 0 || isValidJoinNumber(value)
}
</script>

<template>
  <div class="workspace-inner">
    <Teleport v-if="actionSlot" :to="actionSlot">
      <div class="workspace-actions">
        <StatusPill v-if="canManageSettings" :tone="healthOk ? 'success' : 'warning'"
          :label="healthOk ? '接入检查通过' : `${healthItems.length} 项待补齐`" dot />
        <StatusPill v-if="currentUser?.role !== 'viewer'" :tone="activeAutomationCount ? 'info' : 'neutral'"
          :label="activeAutomationCount ? `${activeAutomationCount} 个启用任务` : '暂无启用任务'" dot />
      </div>
    </Teleport>

    <div class="settings-layout">
      <nav class="panel setup-nav" aria-label="设置步骤">
        <button v-for="(step) in visibleSteps" :key="step.value" type="button" class="setup-step"
          :class="{ active: activeSection === step.value }"
          :aria-current="activeSection === step.value ? 'step' : undefined" @click="jumpTo(step.value)">
          <span class="step-no">
            <UiIcon v-if="step.done" name="check" :size="12" />
            <UiIcon v-else name="alert" :size="12" />
          </span>
          <span class="setup-step-copy">
            <strong>{{ step.title }}</strong>
            <span>{{ step.summary }}</span>
          </span>
        </button>
      </nav>

      <main class="setup-main">
        <div v-if="setupLoadError && canManageSettings" class="banner is-danger" role="alert">
          <UiIcon name="alert" :size="16" />
          <div>
            <strong>配置数据读取失败</strong>
            <p>{{ setupLoadError }}。下方的楼栋、楼层与中控类型可能不完整。</p>
          </div>
          <button type="button" class="btn btn-quiet btn-sm" :disabled="setupLoading" @click="loadSetupData">
            {{ setupLoading ? '正在重试…' : '重试' }}
          </button>
        </div>

        <section v-if="canManageSettings && !healthOk" class="panel" aria-label="接入检查">
          <header class="panel-head">
            <div class="panel-head-copy">
              <p class="eyebrow">阻塞项</p>
              <h2>接入检查</h2>
              <p class="panel-sub">补齐后才能保证操作台显示可信状态。</p>
            </div>
            <StatusPill tone="warning" :label="`${healthItems.length} 项`" dot />
          </header>
          <div class="panel-body">
            <div class="health-list">
              <div v-for="item in healthItems" :key="item.id" class="health-item">
                <span class="dot" :class="`is-${item.tone}`"></span>
                <span class="grow">{{ item.text }}</span>
                <button type="button" class="btn btn-quiet btn-sm" @click="jumpTo(item.section, item.roomId)">
                  {{ item.actionLabel }}
                </button>
              </div>
            </div>
          </div>
        </section>

        <!-- <section v-else-if="canManageSettings" class="banner is-success" role="status">
          <UiIcon name="check" :size="16" />
          <div>
            <strong>接入检查通过</strong>
            <p>所有教室均已接入中控并配置控制点，操作台状态可信。</p>
          </div>
        </section> -->

        <section class="panel">
          <header class="panel-head">
            <div class="settings-section-head is-flush">
              <div>
                <h2>{{ meta.title }}</h2>
                <p>{{ meta.description }}</p>
              </div>
            </div>
          </header>

          <UsersPage v-if="activeSection === 'users' && currentUser" :current-user="currentUser"
            @notify="emit('notify', $event)" />

          <AutomationWorkspace v-if="activeSection === 'automation'" :rooms="rooms" :schedules="schedules"
            :current-user="currentUser" :selected-room-id="selectedRoomId" :target="automationTarget" :now="now"
            @changed="emit('automationChanged')" @notify="emit('notify', $event)" />

          <ManualTasksWorkspace v-if="activeSection === 'manual-tasks'" :rooms="rooms" :manual-tasks="manualTasks"
            :current-user="currentUser" :selected-room-id="selectedRoomId" @changed="emit('manualTasksChanged')"
            @notify="emit('notify', $event)" />

          <div v-if="activeSection === 'appearance'" class="panel-body stack">
            <div class="stack">
              <div class="settings-section-head is-flush">
                <div>
                  <h3>主题</h3>
                  <p class="panel-sub">切换后立即对所有页面生效，并保存在当前浏览器。</p>
                </div>
              </div>
              <div class="theme-grid" role="radiogroup" aria-label="主题">
                <button v-for="option in themeOptions" :key="option.value" type="button" class="theme-option"
                  :class="{ active: theme === option.value }" role="radio" :aria-checked="theme === option.value"
                  @click="selectTheme(option.value)">
                  <span class="theme-swatch" :style="{ background: option.swatch }"></span>
                  <span>{{ option.label }}</span>
                  <UiIcon v-if="theme === option.value" name="check" :size="14" />
                </button>
              </div>
            </div>

            <hr class="divider" />

            <div class="stack">
              <div class="settings-section-head is-flush">
                <div>
                  <h3>显示密度</h3>
                  <p class="panel-sub">
                    紧凑模式压缩行高与字号，并隐藏工作区页头、把页头操作并入顶部栏；保存在当前浏览器。
                  </p>
                </div>
              </div>
              <div class="segmented" role="radiogroup" aria-label="显示密度">
                <button v-for="option in densityOptions" :key="option.value" type="button"
                  :class="{ active: density === option.value }" role="radio" :aria-checked="density === option.value"
                  @click="selectDensity(option.value)">
                  {{ option.label }}
                </button>
              </div>
            </div>
          </div>

          <div v-if="activeSection === 'branding'" class="panel-body stack">
            <form class="branding-grid" @submit.prevent="saveBranding">
              <div class="logo-preview">
                <BrandLogo :logo-data-url="brandingForm.logoDataUrl"
                  :label="brandingForm.displayName || 'RoomControl'" />
                <div class="logo-actions">
                  <label class="btn btn-outline btn-sm">
                    上传 Logo
                    <input name="logo" type="file" accept="image/png,image/jpeg,image/svg+xml,image/webp" hidden
                      @change="onLogoSelected" />
                  </label>
                  <button v-if="brandingForm.logoDataUrl" type="button" class="btn btn-danger-quiet btn-sm"
                    @click="brandingForm.logoDataUrl = null">
                    清除 Logo
                  </button>
                </div>
                <p class="form-note">正方形图片，不超过 200 KB。</p>
              </div>
              <div class="branding-fields">
                <label class="field">
                  显示名称
                  <input name="displayName" v-model="brandingForm.displayName" class="input" maxlength="60" required />
                </label>
                <label class="field">
                  副标题
                  <input name="subtitle" v-model="brandingForm.subtitle" class="input" maxlength="80"
                    placeholder="例如：教室中控平台" />
                </label>
                <div class="form-actions is-start">
                  <button class="btn btn-primary" :disabled="busy">保存站点信息</button>
                </div>
              </div>
            </form>

            <hr class="divider" />

            <div class="stack">
              <div class="settings-section-head is-flush">
                <div>
                  <h3>操作日志</h3>
                  <p class="panel-sub">
                    超过保留天数的记录每天凌晨 3 点自动清理，保存设置后立即清理一次；0 表示永久保留。
                  </p>
                </div>
              </div>
              <form class="retention-form" @submit.prevent="saveLogRetention">
                <label class="field">
                  保留天数
                  <input name="logRetentionDays" v-model.number="logRetentionInput" class="input" type="number" min="0"
                    :max="MAX_LOG_RETENTION_DAYS" step="1" />
                </label>
                <button class="btn btn-primary" :disabled="busy || logRetentionInput === logRetentionDays">
                  保存日志设置
                </button>
              </form>
              <p v-if="logRetentionError" class="form-note is-warning">{{ logRetentionError }}</p>
            </div>
          </div>

          <div v-if="activeSection === 'locations'" class="panel-body">
            <form class="location-add" @submit.prevent="createBuilding">
              <input name="buildingName" v-model="buildingForm.name" class="input" placeholder="新增楼栋，如：A 栋"
                maxlength="60" required />
              <button class="btn btn-primary" :disabled="busy">添加楼栋</button>
            </form>

            <div v-if="buildings.length" class="location-list">
              <article v-for="building in buildings" :key="building.id" class="location-card">
                <header class="location-head">
                  <template v-if="editingBuildingId === building.id">
                    <input name="buildingEditName" v-model="buildingEditName" class="input location-name-input"
                      maxlength="60" required />
                    <button class="btn btn-primary btn-sm" :disabled="busy" @click="saveBuilding(building)">保存</button>
                    <button class="btn btn-quiet btn-sm" @click="editingBuildingId = ''">取消</button>
                  </template>
                  <template v-else>
                    <strong>{{ building.name }}</strong>
                    <span class="location-count">{{ building.floors.length }} 层</span>
                    <div class="location-actions">
                      <button class="btn btn-quiet btn-sm" @click="startEditBuilding(building)">重命名</button>
                      <button class="btn btn-danger-quiet btn-sm" @click="deleteBuilding(building)">删除</button>
                    </div>
                  </template>
                </header>

                <div class="floor-list">
                  <div v-for="floor in building.floors" :key="floor.id" class="floor-chip">
                    <template v-if="editingFloorId === floor.id">
                      <input name="floorEditName" v-model="floorEditName" class="input floor-chip-input" maxlength="60"
                        required />
                      <button class="btn btn-primary btn-sm" :disabled="busy" @click="saveFloor(floor)">保存</button>
                      <button class="btn btn-quiet btn-sm" @click="editingFloorId = ''">取消</button>
                    </template>
                    <template v-else>
                      <span>{{ floor.name }}</span>
                      <button type="button" class="chip-action" title="重命名楼层" @click="startEditFloor(floor)">
                        <UiIcon name="edit" :size="12" />
                      </button>
                      <button type="button" class="chip-action is-danger" title="删除楼层"
                        @click="deleteFloor(building, floor)">
                        <UiIcon name="x" :size="12" />
                      </button>
                    </template>
                  </div>
                  <form class="floor-add" @submit.prevent="createFloor(building)">
                    <input name="floorDraft" v-model="floorDrafts[building.id]" class="input" placeholder="新增楼层，如：5F"
                      maxlength="60" />
                    <button class="btn btn-outline btn-sm" :disabled="busy || !floorDrafts[building.id]">添加</button>
                  </form>
                </div>
              </article>
            </div>
            <EmptyState v-else icon="room" title="尚未配置楼栋" description="添加楼栋后即可为其添加楼层，之后创建教室时选择。" />
          </div>

          <div v-if="activeSection === 'rooms'" class="panel-body">
            <form class="room-create-form" @submit.prevent="createRoom">
              <label class="field">
                教室名称
                <input name="roomName" v-model="roomForm.name" class="input" placeholder="例如：A101" maxlength="120"
                  required />
              </label>
              <label class="field">
                楼栋
                <select name="building" v-model="roomForm.building" class="select" :disabled="!buildings.length"
                  required>
                  <option v-if="!buildings.length" value="">请先添加楼栋</option>
                  <option v-for="building in buildings" :key="building.id" :value="building.name">
                    {{ building.name }}
                  </option>
                </select>
              </label>
              <label class="field">
                楼层
                <select name="floor" v-model="roomForm.floor" class="select" :disabled="!createFloorOptions.length"
                  required>
                  <option v-if="!createFloorOptions.length" value="">请先添加楼层</option>
                  <option v-for="floor in createFloorOptions" :key="floor.id" :value="floor.name">
                    {{ floor.name }}
                  </option>
                </select>
              </label>
              <button class="btn btn-primary" :disabled="busy || !createFloorOptions.length">添加教室</button>
            </form>
            <p v-if="!buildings.length" class="form-note is-warning">
              添加教室前，请先在「楼栋与楼层」中配置楼栋和楼层。
            </p>

            <div v-if="sortedRooms.length" class="room-table">
              <div class="room-table-head">
                <span>教室</span><span>楼栋 / 楼层</span><span>状态</span><span>操作</span>
              </div>
              <template v-for="room in sortedRooms" :key="room.id">
                <div v-if="editingRoomId === room.id" class="room-row is-editing">
                  <input name="roomName" v-model="roomEdit.name" class="input" maxlength="120" required />
                  <div class="room-row-split">
                    <select name="building" v-model="roomEdit.building" class="select" required>
                      <option v-if="!buildings.some((building) => building.name === roomEdit.building)" disabled
                        :value="roomEdit.building">
                        {{ roomEdit.building || '请选择楼栋' }}
                      </option>
                      <option v-for="building in buildings" :key="building.id" :value="building.name">
                        {{ building.name }}
                      </option>
                    </select>
                    <select name="floor" v-model="roomEdit.floor" class="select" required>
                      <option v-if="!editFloorOptions.some((floor) => floor.name === roomEdit.floor)" disabled
                        :value="roomEdit.floor">
                        {{ roomEdit.floor || '请选择楼层' }}
                      </option>
                      <option v-for="floor in editFloorOptions" :key="floor.id" :value="floor.name">
                        {{ floor.name }}
                      </option>
                    </select>
                  </div>
                  <span class="text-xs muted">保存立即生效</span>
                  <div class="room-row-actions">
                    <button class="btn btn-primary btn-sm" :disabled="busy" @click="saveRoom(room)">保存</button>
                    <button class="btn btn-quiet btn-sm" @click="editingRoomId = ''">取消</button>
                  </div>
                </div>
                <div v-else class="room-row">
                  <strong>{{ room.name }}</strong>
                  <span>{{ buildingOf(room) }} · {{ floorOf(room) }}</span>
                  <StatusPill :tone="roomStatusTone(room)" :label="roomStatusLabel(room)" dot />
                  <div class="room-row-actions">
                    <button class="btn btn-quiet btn-sm" @click="startEditRoom(room)">编辑</button>
                    <button class="btn btn-danger-quiet btn-sm" @click="deleteRoom(room)">删除</button>
                  </div>
                </div>
              </template>
            </div>
            <EmptyState v-else icon="room" title="还没有教室" description="先配置楼栋与楼层，再创建教室。" />
          </div>

          <div v-if="activeSection === 'controllers'" class="panel-body">
            <div class="controller-layout">
              <div class="controller-room-list">
                <div v-if="sortedRooms.length" class="controller-search">
                  <RoomCascader stacked :rooms="rooms" :model-value="configuredRoomId"
                    @update:model-value="configuredRoomId = $event" />
                </div>
                <RoomTree :groups="controllerGroups" :selected-id="configuredRoomId" label="选择教室"
                  @select="configuredRoomId = $event">
                  <template #status="{ room }">
                    <span class="dot" :class="`is-${roomStatusTone(room)}`"></span>
                  </template>
                  <template #meta="{ room }">{{ room.controller?.joins.length ?? 0 }}</template>
                </RoomTree>
              </div>

              <div v-if="configuredRoom" class="controller-panel">
                <div class="settings-section-head is-flush">
                  <div>
                    <p class="eyebrow">
                      {{ buildingOf(configuredRoom) }} / {{ floorOf(configuredRoom) }}
                    </p>
                    <h3>{{ configuredRoom.name }}</h3>
                    <p class="panel-sub">
                      <template v-if="configuredRoom.controller">
                        {{ configuredRoom.controller.name }} · {{ configuredRoom.controller.host }}:{{
                          configuredRoom.controller.port
                        }}
                        · IPID {{ configuredRoom.controller.ipid }}
                      </template>
                      <template v-else>尚未接入中控</template>
                    </p>
                  </div>
                  <StatusPill :tone="roomStatusTone(configuredRoom)" :label="roomStatusLabel(configuredRoom)" dot />
                </div>

                <div v-if="configuredRoom.controller?.lastError" class="banner is-warning" role="status">
                  <UiIcon name="alert" :size="16" />
                  <div>
                    <strong>最近一次连接错误</strong>
                    <p>{{ configuredRoom.controller.lastError }}</p>
                  </div>
                </div>

                <form class="controller-form-grid" @submit.prevent="saveController">
                  <label class="field">
                    中控类型
                    <select name="adapterType" v-model="controllerForm.adapterType" class="select" required>
                      <option v-for="type in controllerTypes" :key="type.key" :value="type.key">
                        {{ type.displayName }}
                      </option>
                    </select>
                  </label>
                  <label class="field">
                    主机地址
                    <input name="host" v-model="controllerForm.host" class="input" required />
                  </label>
                  <label class="field">
                    TCP 端口
                    <input name="port" v-model.number="controllerForm.port" class="input" type="number" min="1"
                      max="65535" required />
                  </label>
                  <label class="field">
                    客户端 IPID
                    <input name="ipid" v-model.number="controllerForm.ipid" class="input" type="number" min="0"
                      max="255" required />
                  </label>
                  <label v-if="configuredRoom.controller" class="checkbox">
                    <input name="enabled" v-model="controllerForm.enabled" type="checkbox" />
                    启用连接
                  </label>
                  <div class="controller-form-actions">
                    <button class="btn btn-primary" :disabled="busy">
                      {{ configuredRoom.controller ? '保存配置' : '保存并连接' }}
                    </button>
                    <button v-if="configuredRoom.controller" type="button" class="btn btn-danger-quiet btn-sm"
                      :disabled="busy" @click="deleteController">
                      移除中控
                    </button>
                  </div>
                </form>
                <p class="form-note">保存后后台会自动重连；状态以中控实际回报为准，尚未回报时按低电平（关闭）显示。</p>

                <div class="join-config">
                  <div class="join-config-heading">
                    <div>
                      <h3>控制点</h3>
                      <p class="panel-sub">拖动左侧手柄可调整操作台中的按钮顺序。</p>
                    </div>
                    <div class="row">
                      <span class="text-xs muted">{{ configuredRoom.controller?.joins.length ?? 0 }} 个</span>
                      <button v-if="configuredRoom.controller && joinRows.length" type="button"
                        class="btn btn-quiet btn-sm" @click="openCopyAll">
                        全部复制到教室
                      </button>
                      <button v-if="configuredRoom.controller" type="button" class="btn btn-primary btn-sm"
                        @click="openCreateJoin">
                        <UiIcon name="plus" :size="14" />添加控制点
                      </button>
                    </div>
                  </div>

                  <template v-if="configuredRoom.controller">
                    <div v-if="joinRows.length" class="join-config-list">
                      <div v-for="join in joinRows" :key="join.id" class="join-config-row" :class="{
                        'is-dragging': draggingJoinId === join.id,
                        'is-drag-over': dragOverJoinId === join.id,
                      }" @dragover="dragJoinOver($event, join)" @dragleave="clearJoinDragOver(join)"
                        @drop="dropJoin($event, join)">
                        <span class="join-type-icon join-drag-handle" draggable="true" title="拖动调整控制台按钮顺序"
                          aria-label="拖动调整顺序" @dragstart="startJoinDrag($event, join)" @dragend="endJoinDrag">
                          <UiIcon name="drag" :size="14" />
                        </span>
                        <strong>{{ join.name }}</strong>
                        <span class="join-kind">
                          发送 {{ join.sendJoinNumber }} · 状态 {{ join.stateJoinNumber }} · 使能
                          {{ join.enableJoinNumber }} · {{ join.showConfirmationPopup ? '需确认' : '直接下发' }}
                        </span>
                        <div class="join-config-actions">
                          <button class="btn btn-quiet btn-sm" @click="openCopyJoin(join)">复制到教室</button>
                          <button class="btn btn-quiet btn-sm" @click="openEditJoin(join)">编辑</button>
                          <button class="btn btn-danger-quiet btn-sm" @click="deleteJoin(join)">删除</button>
                        </div>
                      </div>
                    </div>
                    <p v-else class="empty-history text-sm muted">
                      该中控还没有控制点，点击「添加控制点」创建 Join 映射后即可在操作台操作。
                    </p>
                  </template>

                  <EmptyState v-else icon="connection" title="尚未配置中控" description="先保存中控连接信息，之后即可添加 CIP 控制点。" />
                </div>
              </div>

              <EmptyState v-else icon="room" title="请先添加教室" description="创建教室后才能配置中控与设备。">
                <button type="button" class="btn btn-outline btn-sm empty-action" @click="jumpTo('rooms')">
                  去添加教室
                </button>
              </EmptyState>
            </div>
          </div>
        </section>
      </main>
    </div>

    <div v-if="joinDialogOpen" class="dialog-overlay" @click.self="closeJoinDialog"
      @keydown.esc.stop.prevent="closeJoinDialog">
      <section class="dialog" role="dialog" aria-modal="true" aria-labelledby="join-form-title">
        <h2 id="join-form-title">{{ joinDialogTargetId ? '编辑控制点' : '添加控制点' }}</h2>
        <p class="panel-sub">
          分别配置发送、状态接收与使能接收 Join；使能 Join 填 0 表示不设置并默认允许，只限制手动操作。
        </p>
        <form @submit.prevent="submitJoin">
          <div class="dialog-body stack">
            <label class="field">
              命令名称
              <input name="join-name" autocomplete="off" v-model="joinForm.name" class="input" placeholder="例如：上课"
                maxlength="120" required />
            </label>
            <div class="join-number-fields">
              <label class="field">
                发送 Join
                <input name="sendJoinNumber" v-model.number="joinForm.sendJoinNumber" class="input" type="number"
                  min="1" max="32768" required />
              </label>
              <label class="field">
                状态接收
                <input name="stateJoinNumber" v-model.number="joinForm.stateJoinNumber" class="input" type="number"
                  min="1" max="32768" required />
              </label>
              <label class="field">
                使能接收
                <input name="enableJoinNumber" v-model.number="joinForm.enableJoinNumber" class="input" type="number"
                  min="0" max="32768" placeholder="0 = 不设置" />
              </label>
            </div>
            <label class="checkbox" title="发送指令或删除该控制点前显示确认弹窗">
              <input name="showConfirmationPopup" v-model="joinForm.showConfirmationPopup" type="checkbox" />
              操作前确认
            </label>
          </div>
          <div class="form-actions">
            <button type="button" class="btn btn-quiet" @click="closeJoinDialog">取消</button>
            <button type="submit" class="btn btn-primary" :disabled="busy">{{ busy ? '保存中…' : '保存' }}</button>
          </div>
        </form>
      </section>
    </div>

    <div v-if="copyingJoin || copyingAll" class="dialog-overlay" @click.self="closeCopyJoin"
      @keydown.esc.stop.prevent="closeCopyJoin">
      <section class="dialog is-wide" role="dialog" aria-modal="true" aria-labelledby="copy-join-title">
        <h2 id="copy-join-title">{{ copyingAll ? '全部复制控制点到教室' : '复制控制点到教室' }}</h2>
        <p v-if="copyingAll">
          将复制当前教室的全部控制点。目标教室原有控制点会全部删除，关联的自动任务会一并删除。
        </p>
        <p v-else>
          将“{{ copyingJoin?.name }}”复制到选中的教室；若发送 Join 冲突，将覆盖目标教室中的同 Join 控制点。
        </p>
        <template v-if="copyTargetRooms.length">
          <div class="copy-targets-head">
            <span class="text-xs muted">
              已选 {{ copyTargetControllerIds.length }} / {{ copyTargetRooms.length }} 间教室
            </span>
            <div class="copy-targets-actions">
              <button type="button" class="btn btn-quiet btn-sm" @click="selectAllCopyTargets">全选</button>
              <button type="button" class="btn btn-quiet btn-sm" @click="clearCopyTargets">清空</button>
            </div>
          </div>
          <div class="copy-targets">
            <label v-for="room in copyTargetRooms" :key="room.id" class="checkbox">
              <input name="controllerId" v-model="copyTargetControllerIds" type="checkbox"
                :value="room.controller!.id" />
              {{ buildingOf(room) }} / {{ floorOf(room) }} / {{ room.name }}
            </label>
          </div>
        </template>
        <EmptyState v-else icon="connection" title="暂无其他已配置中控的教室" description="先为其他教室接入中控后再复制。" />
        <div class="dialog-actions">
          <button type="button" class="btn btn-quiet" @click="closeCopyJoin">取消</button>
          <button type="button" class="btn btn-primary" :disabled="busy || copyTargetControllerIds.length === 0"
            @click="copyJoin">
            {{ busy ? '复制中…' : '确认复制' }}
          </button>
        </div>
      </section>
    </div>

    <ConfirmDialog :open="confirmation !== null" :title="confirmation?.title ?? ''"
      :message="confirmation?.message ?? ''" :confirm-label="confirmation?.actionLabel ?? '确认'"
      :tone="confirmation?.tone ?? 'danger'" @cancel="resolveConfirmation(false)"
      @confirm="resolveConfirmation(true)" />
  </div>
</template>
