<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { readJson, request, toErrorMessage } from '../api'
import { formatClock, formatDateTime, formatRelative } from '../lib/format'
import { collator } from '../lib/rooms'
import { groupSchedules, type ScheduleGroup } from '../lib/schedules'
import { scheduleKinds } from '../types'
import type {
  AutomationTarget,
  CurrentUser,
  JoinMapping,
  Room,
  Schedule,
  ScheduleExecution,
  ScheduleKind,
} from '../types'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import EmptyState from './ui/EmptyState.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'

// 自动任务面板：先看计划与执行结果，再创建、修改、暂停、恢复或删除任务。
const props = defineProps<{
  rooms: Room[]
  schedules: Schedule[]
  currentUser: CurrentUser | null
  selectedRoomId: string
  target: AutomationTarget | null
  now: Date
}>()

const emit = defineEmits<{
  changed: []
  notify: [payload: { message: string; isError: boolean }]
}>()

// 任务筛选的唯一定义来源：新增筛选项只需修改这里。
const taskStatusFilters = [
  { value: 'all', label: '全部' },
  { value: 'enabled', label: '启用中' },
  { value: 'disabled', label: '已暂停' },
] as const

type StatusFilter = (typeof taskStatusFilters)[number]['value']

interface TaskExecution extends ScheduleExecution {
  roomName: string
}

const taskQuery = ref('')
const roomFilter = ref('')
const statusFilter = ref<StatusFilter>('all')

const openTaskKey = ref('')
const focusTaskKey = ref('')
const formOpen = ref(false)
const historyCache = reactive<Record<string, TaskExecution[]>>({})
const historyLoadingKey = ref('')
const historyError = ref('')

const initialRunAt = new Date(Date.now() + 10 * 60_000)
const form = reactive({
  mode: 'create' as 'create' | 'edit',
  editingGroupId: '',
  roomIds: [] as string[],
  joinName: '',
  name: '',
  kind: 'Weekly' as ScheduleKind,
  runDate: toLocalDateInput(initialRunAt),
  runHour: initialRunAt.getHours(),
  runMinute: initialRunAt.getMinutes(),
  weekdays: [1, 2, 3, 4, 5] as number[],
  localHour: 8,
  localMinute: 30,
})

const formBusy = ref(false)
const pendingDelete = ref<ScheduleGroup | null>(null)
const deleteBusy = ref(false)
const stateBusyKey = ref('')

const weekdayOptions = [
  { value: 1, label: '一' },
  { value: 2, label: '二' },
  { value: 3, label: '三' },
  { value: 4, label: '四' },
  { value: 5, label: '五' },
  { value: 6, label: '六' },
  { value: 0, label: '日' },
]

const hourOptions = Array.from({ length: 24 }, (_, hour) => hour)
const minuteOptions = Array.from({ length: 60 }, (_, minute) => minute)

const canOperate = computed(() => props.currentUser?.role !== 'viewer')

const schedulableRooms = computed(() =>
  props.rooms.filter((room) => room.controller && room.controller.joins.length > 0),
)

const tasks = computed(() => groupSchedules(props.schedules))

const roomOptions = computed(() =>
  [...new Set(tasks.value.flatMap((task) => task.roomNames))].sort(collator.compare),
)

const filteredTasks = computed(() => {
  const query = taskQuery.value.trim().toLocaleLowerCase()
  return tasks.value
    .filter((task) => {
      if (roomFilter.value && !task.roomNames.includes(roomFilter.value)) return false
      if (statusFilter.value === 'enabled' && !task.enabled) return false
      if (statusFilter.value === 'disabled' && task.enabled) return false
      if (!query) return true
      return `${task.name} ${task.joinName} ${task.roomNames.join(' ')}`
        .toLocaleLowerCase()
        .includes(query)
    })
    .sort((left, right) => {
      if (left.enabled !== right.enabled) return left.enabled ? -1 : 1
      const leftNext = left.nextRunAtUtc ? new Date(left.nextRunAtUtc).getTime() : Number.MAX_SAFE_INTEGER
      const rightNext = right.nextRunAtUtc ? new Date(right.nextRunAtUtc).getTime() : Number.MAX_SAFE_INTEGER
      return leftNext - rightNext || collator.compare(left.name, right.name)
    })
})

const selectedRooms = computed(() =>
  schedulableRooms.value.filter((room) => form.roomIds.includes(room.id)),
)

// 控制点按名称对齐：只有所选教室都具备的名称才能一次覆盖多间教室。
const commonJoinNames = computed(() => {
  const rooms = selectedRooms.value
  if (!rooms.length) return []
  const [first, ...rest] = rooms
  const names = (first.controller?.joins ?? []).map((join) => join.name)
  return names
    .filter((name) => rest.every((room) => room.controller?.joins.some((join) => join.name === name)))
    .sort(collator.compare)
})

const selectedTargets = computed(() =>
  selectedRooms.value
    .map((room) => room.controller?.joins.find((join) => join.name === form.joinName))
    .filter((join): join is JoinMapping => Boolean(join)),
)

function toLocalDateInput(date: Date) {
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 10)
}

function pad2(value: number) {
  return String(value).padStart(2, '0')
}

// 日期和时间分开存储；提交时再拼成后端需要的本地日期时间。
function runAtLocalValue() {
  return `${form.runDate}T${pad2(form.runHour)}:${pad2(form.runMinute)}`
}

function localTimeValue() {
  return `${pad2(form.localHour)}:${pad2(form.localMinute)}:00`
}

function taskRoomsLabel(task: ScheduleGroup) {
  if (task.roomNames.length <= 2) return task.roomNames.join('、')
  return `${task.roomNames.slice(0, 2).join('、')} 等 ${task.roomNames.length} 间`
}

function ensureRoomSelection() {
  const available = schedulableRooms.value
  form.roomIds = form.roomIds.filter((roomId) => available.some((room) => room.id === roomId))
  if (form.roomIds.length) return
  const preferred = available.find((room) => room.id === props.selectedRoomId)
  const initial = preferred ?? available[0]
  form.roomIds = initial ? [initial.id] : []
}

function toggleRoom(roomId: string) {
  form.roomIds = form.roomIds.includes(roomId)
    ? form.roomIds.filter((value) => value !== roomId)
    : [...form.roomIds, roomId]
}

function selectAllRooms() {
  form.roomIds = schedulableRooms.value.map((room) => room.id)
}

function clearRooms() {
  form.roomIds = []
}

watch(
  () => props.selectedRoomId,
  (roomId) => {
    if (form.mode === 'edit' || !roomId) return
    if (schedulableRooms.value.some((room) => room.id === roomId)) form.roomIds = [roomId]
  },
  { immediate: true },
)

watch(
  schedulableRooms,
  () => ensureRoomSelection(),
  { immediate: true },
)

watch(
  commonJoinNames,
  (names) => {
    if (!names.includes(form.joinName)) form.joinName = names[0] ?? ''
  },
  { immediate: true },
)

function toggleWeekday(day: number) {
  if (form.weekdays.includes(day)) {
    form.weekdays = form.weekdays.filter((value) => value !== day)
  } else {
    form.weekdays = [...form.weekdays, day].sort((left, right) => left - right)
  }
}

function planLabel(task: { kind: ScheduleKind }) {
  return scheduleKinds.find((kind) => kind.value === task.kind)?.label ?? task.kind
}

function isOverdue(value: string | null) {
  return value !== null && new Date(value).getTime() < props.now.getTime() - 60_000
}

async function loadHistory(task: ScheduleGroup, force = false) {
  if (!force && historyCache[task.key]) return
  historyLoadingKey.value = task.key
  historyError.value = ''
  try {
    const perTarget = await Promise.all(
      task.targets.map(async (target) => {
        const executions = await readJson<ScheduleExecution[]>(`/api/schedules/${target.id}/executions`)
        return executions.map((execution) => ({ ...execution, roomName: target.roomName }))
      }),
    )
    historyCache[task.key] = perTarget
      .flat()
      .sort((left, right) => new Date(right.scheduledAtUtc).getTime() - new Date(left.scheduledAtUtc).getTime())
  } catch (error) {
    historyError.value = toErrorMessage(error)
  } finally {
    historyLoadingKey.value = ''
  }
}

async function toggleHistory(task: ScheduleGroup) {
  if (openTaskKey.value === task.key) {
    openTaskKey.value = ''
    return
  }
  openTaskKey.value = task.key
  await loadHistory(task)
}

function executionTone(status: string) {
  if (status === 'Succeeded') return 'success' as const
  if (status === 'Failed') return 'danger' as const
  return 'info' as const
}

function executionLabel(status: string) {
  if (status === 'Succeeded') return '已发送'
  if (status === 'Failed') return '失败'
  return '待执行'
}

function resetForm() {
  form.mode = 'create'
  form.editingGroupId = ''
  form.name = ''
  form.kind = 'Weekly'
  form.weekdays = [1, 2, 3, 4, 5]
  form.localHour = 8
  form.localMinute = 30
  const defaultRunAt = new Date(Date.now() + 10 * 60_000)
  form.runDate = toLocalDateInput(defaultRunAt)
  form.runHour = defaultRunAt.getHours()
  form.runMinute = defaultRunAt.getMinutes()
  form.joinName = commonJoinNames.value[0] ?? ''
}

function openCreate() {
  resetForm()
  formOpen.value = true
  void nextTick(() => document.getElementById('auto-form')?.focus())
}

function closeForm() {
  formOpen.value = false
  resetForm()
}

// 表单是悬浮对话框：Esc 随时可关闭，焦点移入面板以便键盘操作。
function onEscape(event: KeyboardEvent) {
  if (event.key !== 'Escape' || !formOpen.value) return
  event.stopPropagation()
  closeForm()
}

watch(formOpen, (open) => {
  if (open) window.addEventListener('keydown', onEscape)
  else window.removeEventListener('keydown', onEscape)
})

onBeforeUnmount(() => window.removeEventListener('keydown', onEscape))

async function submitForm() {
  if (!form.roomIds.length) {
    emit('notify', { message: '请至少选择一间教室。', isError: true })
    return
  }
  if (!form.joinName || !selectedTargets.value.length) {
    emit('notify', { message: '所选教室没有同名控制点，请调整教室范围。', isError: true })
    return
  }
  if (!form.name.trim()) {
    emit('notify', { message: '请填写任务名称。', isError: true })
    return
  }
  if (form.kind === 'Weekly' && form.weekdays.length === 0) {
    emit('notify', { message: '每周任务至少需要选择一个重复日。', isError: true })
    return
  }

  const weekdayMask = form.weekdays.reduce((mask, day) => mask | (1 << day), 0)
  const body = {
    joinMappingIds: selectedTargets.value.map((join) => join.id),
    name: form.name.trim(),
    kind: form.kind,
    runAtLocal: form.kind === 'Once' ? runAtLocalValue() : null,
    weekdayMask: form.kind === 'Weekly' ? weekdayMask : 0,
    localTime: form.kind === 'Weekly' ? localTimeValue() : '00:00:00',
  }

  const editing = form.mode === 'edit' && form.editingGroupId
  formBusy.value = true
  try {
    await request(editing ? `/api/schedules/groups/${form.editingGroupId}` : '/api/schedules', {
      method: editing ? 'PUT' : 'POST',
      body: JSON.stringify(body),
    })
    emit('notify', {
      message: editing
        ? `任务「${body.name}」已更新`
        : `任务「${body.name}」已创建，覆盖 ${body.joinMappingIds.length} 间教室`,
      isError: false,
    })
    resetForm()
    formOpen.value = false
    emit('changed')
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    formBusy.value = false
  }
}

function startEdit(task: ScheduleGroup) {
  formOpen.value = true
  form.mode = 'edit'
  form.editingGroupId = task.groupId ?? task.targets[0]?.id ?? ''
  form.roomIds = task.targets
    .map((target) => target.roomId)
    .filter((roomId): roomId is string => Boolean(roomId))
  form.joinName = task.joinName
  form.name = task.name
  form.kind = task.kind
  form.weekdays = [0, 1, 2, 3, 4, 5, 6].filter((day) => (task.weekdayMask & (1 << day)) !== 0)
  const [localHour = '08', localMinute = '30'] = (task.localTime ?? '08:30:00').split(':')
  form.localHour = Number(localHour)
  form.localMinute = Number(localMinute)
  const runAt = task.runAtUtc ? new Date(task.runAtUtc) : new Date(Date.now() + 10 * 60_000)
  form.runDate = toLocalDateInput(runAt)
  form.runHour = runAt.getHours()
  form.runMinute = runAt.getMinutes()
  void nextTick(() => document.getElementById('auto-form')?.focus())
}

function requestDelete(task: ScheduleGroup) {
  pendingDelete.value = task
}

async function confirmDelete() {
  const task = pendingDelete.value
  if (!task) return
  const groupId = task.groupId ?? task.targets[0]?.id
  if (!groupId) return
  deleteBusy.value = true
  try {
    await request(`/api/schedules/groups/${groupId}`, { method: 'DELETE' })
    if (form.editingGroupId === groupId) closeForm()
    pendingDelete.value = null
    historyCache[task.key] = []
    openTaskKey.value = ''
    emit('notify', { message: `任务「${task.name}」已删除`, isError: false })
    emit('changed')
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    deleteBusy.value = false
  }
}

// 暂停与恢复共用同一组端点，恢复时由后端按当前时间重算下一次执行。
async function toggleTaskState(task: ScheduleGroup) {
  const groupId = task.groupId ?? task.targets[0]?.id
  if (!groupId) return
  stateBusyKey.value = task.key
  try {
    await request(`/api/schedules/groups/${groupId}/${task.enabled ? 'pause' : 'resume'}`, {
      method: 'POST',
    })
    emit('notify', {
      message: `任务「${task.name}」已${task.enabled ? '暂停' : '恢复'}`,
      isError: false,
    })
    emit('changed')
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    stateBusyKey.value = ''
  }
}

function applyTarget() {
  const target = props.target
  if (!target) return

  if (target.roomId && schedulableRooms.value.some((room) => room.id === target.roomId)) {
    formOpen.value = true
    form.mode = 'create'
    form.editingGroupId = ''
    form.roomIds = [target.roomId]
    if (target.joinId) {
      const join = schedulableRooms.value
        .find((room) => room.id === target.roomId)
        ?.controller?.joins.find((item) => item.id === target.joinId)
      if (join) form.joinName = join.name
    }
  }

  if (target.scheduleId) {
    const task = tasks.value.find((item) =>
      item.targets.some((schedule) => schedule.id === target.scheduleId),
    )
    if (task) {
      focusTaskKey.value = task.key
      roomFilter.value = ''
      taskQuery.value = ''
      openTaskKey.value = task.key
      void loadHistory(task)
      void nextTick(() =>
        document
          .getElementById(`auto-task-${task.key}`)
          ?.scrollIntoView({ behavior: 'smooth', block: 'center' }),
      )
    }
  }
}

watch(() => props.target?.token, applyTarget, { immediate: true })
watch(
  () => props.schedules,
  () => {
    if (props.target?.scheduleId && !openTaskKey.value) applyTarget()
  },
)
</script>

<template>
  <div class="workspace-inner">
    <div class="automation-layout">
      <div class="stack">
        <header class="panel-toolbar">
          <div class="auto-toolbar-left">
            <div class="search-field">
              <UiIcon name="search" :size="15" />
              <input name="taskQuery" v-model="taskQuery" class="input" type="search" placeholder="搜索任务/教室"
                aria-label="搜索任务" />
            </div>
            <select name="roomFilter" v-model="roomFilter" class="select auto-room-filter" aria-label="按教室筛选">
              <option value="">全部教室</option>
              <option v-for="room in roomOptions" :key="room" :value="room">{{ room }}</option>
            </select>
            <div class="chip-row" role="group" aria-label="按启用状态筛选">
              <button v-for="option in taskStatusFilters" :key="option.value" type="button" class="chip"
                :class="{ active: statusFilter === option.value }" :aria-pressed="statusFilter === option.value"
                @click="statusFilter = option.value">
                {{ option.label }}
              </button>
            </div>
          </div>
          <div class="auto-toolbar-right">
            <span class="text-xs muted">{{ filteredTasks.length }} / {{ tasks.length }}</span>
            <button type="button" class="btn btn-primary btn-sm" @click="openCreate">
              <UiIcon name="plus" :size="14" />新建任务
            </button>
          </div>
        </header>
        <table class="table">
          <thead>
            <tr>
              <th>任务名称</th>
              <th>教室</th>
              <th>启动时间</th>
              <th>状态</th>
              <th>操作</th>
            </tr>
          </thead>
        </table>
        <div v-if="filteredTasks.length" class="auto-list">
          <article v-for="task in filteredTasks" :id="`auto-task-${task.key}`" :key="task.key" class="auto-task"
            :class="{ 'is-focus': focusTaskKey === task.key }">
            <div class="auto-task-main">
              <div class="auto-task-title">
                <strong>{{ task.name }}</strong>
                <span>{{ planLabel(task) }} · {{ task.joinName }}</span>
              </div>
              <div class="auto-task-cell">
                {{ taskRoomsLabel(task) }}
                <span>{{ task.roomNames.length }} 间教室</span>
              </div>
              <div class="auto-task-cell" :class="{ 'is-overdue': isOverdue(task.nextRunAtUtc) }">
                {{ formatRelative(task.nextRunAtUtc, now.getTime()) }}
                <span>
                  {{ task.nextRunAtUtc ? formatDateTime(task.nextRunAtUtc) : '无计划时间' }}
                </span>
              </div>
              <div>
                <StatusPill :tone="task.enabled ? 'success' : 'neutral'" :label="task.enabled ? '已启用' : '已暂停'" dot />
              </div>
              <div class="auto-task-actions">
                <button type="button" class="btn btn-quiet btn-sm" title="查看执行记录" @click="toggleHistory(task)">
                  {{ openTaskKey === task.key ? '收起' : '记录' }}
                </button>
                <button v-if="canOperate" type="button" class="btn btn-quiet btn-sm" title="修改任务时间、范围与目标"
                  @click="startEdit(task)">
                  编辑
                </button>
                <button v-if="canOperate" type="button" class="btn btn-quiet btn-sm"
                  :disabled="stateBusyKey === task.key" :title="task.enabled ? '暂停后不再按计划执行，可随时恢复' : '按当前时间重新排定执行'"
                  @click="toggleTaskState(task)">
                  {{ task.enabled ? '暂停' : '恢复' }}
                </button>
                <button v-if="canOperate" type="button" class="btn btn-danger-quiet btn-sm" title="删除任务及其执行记录"
                  @click="requestDelete(task)">
                  删除
                </button>
              </div>
            </div>

            <section v-if="openTaskKey === task.key" class="execution-panel">
              <div class="execution-head">
                <span>执行记录</span>
                <button type="button" class="btn btn-ghost btn-sm" :disabled="historyLoadingKey === task.key"
                  @click="loadHistory(task, true)">
                  <UiIcon name="refresh" :size="13" />刷新
                </button>
              </div>
              <div v-if="historyLoadingKey === task.key" class="loading-inline">
                <span class="spinner"></span>正在读取执行记录…
              </div>
              <p v-else-if="historyError" class="execution-error" role="alert">{{ historyError }}</p>
              <template v-else-if="historyCache[task.key]?.length">
                <div v-for="execution in historyCache[task.key]" :key="execution.id" class="execution-row">
                  <StatusPill :tone="executionTone(execution.status)" :label="executionLabel(execution.status)" />
                  <span class="tabular">
                    {{ formatDateTime(execution.scheduledAtUtc) }}
                  </span>
                  <span>
                    <span v-if="execution.error" class="execution-error">
                      {{ execution.roomName }} · {{ execution.error }}
                    </span>
                    <template v-else>{{ execution.roomName }} · 数字控制指令已下发</template>
                  </span>
                  <span class="text-xs muted tabular">
                    {{
                      execution.completedAtUtc
                        ? formatClock(execution.completedAtUtc)
                        : '—'
                    }}
                  </span>
                </div>
              </template>
              <p v-else class="execution-empty">暂无执行记录。任务触发后会在此显示结果。</p>
            </section>
          </article>
        </div>

        <EmptyState v-else icon="calendar" title="没有符合条件的任务" description="调整筛选条件，或新建任务为教室控制点安排计划。" />

      </div>

      <Teleport to="body">
        <div v-if="formOpen" class="dialog-overlay auto-form-overlay" @click.self="closeForm"
          @keydown.esc.stop.prevent="closeForm">
          <aside id="auto-form" class="panel auto-form" role="dialog" aria-modal="true"
            aria-labelledby="auto-form-title" tabindex="-1">
            <header class="panel-head">
              <div class="panel-head-copy">
                <h2 id="auto-form-title">{{ form.mode === 'edit' ? '修改执行计划' : '新建执行计划' }}</h2>
                <p class="panel-sub">一处配置即可覆盖多间教室的同名控制点。</p>
              </div>
              <button type="button" class="btn btn-quiet btn-sm" title="关闭表单" aria-label="关闭表单" @click="closeForm">
                <UiIcon name="x" :size="15" />
              </button>
            </header>

            <form v-if="schedulableRooms.length" class="panel-body" @submit.prevent="submitForm">
              <div class="field">
                教室（已选 {{ form.roomIds.length }} / {{ schedulableRooms.length }}）
                <div class="room-picker">
                  <label v-for="room in schedulableRooms" :key="room.id" class="room-option">
                    <input name="roomIds" type="checkbox" :checked="form.roomIds.includes(room.id)"
                      @change="toggleRoom(room.id)" />
                    <span class="room-option-copy">
                      <strong>{{ room.name }}</strong>
                      <span>{{ room.building || '未分配楼栋' }} · {{ room.floor || '未分配楼层' }}</span>
                    </span>
                  </label>
                </div>
                <div class="room-picker-actions">
                  <button type="button" class="btn btn-quiet btn-sm" @click="selectAllRooms">全选</button>
                  <button type="button" class="btn btn-quiet btn-sm" @click="clearRooms">清空</button>
                </div>
              </div>
              <div>
                <label class="field">
                  控制点（所选教室共有）
                  <select name="joinName" v-model="form.joinName" class="select" :disabled="!commonJoinNames.length">
                    <option v-for="name in commonJoinNames" :key="name" :value="name">{{ name }}</option>
                  </select>
                </label>
                <p v-if="form.roomIds.length && !commonJoinNames.length" class="form-note is-warning">
                  所选教室没有同名控制点，请调整教室范围。
                </p>

                <label class="field">
                  任务名称
                  <input name="taskName" v-model="form.name" class="input" maxlength="120" placeholder="例如：工作日上课"
                    required />
                </label>

                <div class="field">
                  重复方式
                  <div class="segmented" role="group" aria-label="重复方式">
                    <button v-for="kind in scheduleKinds" :key="kind.value" type="button"
                      :class="{ active: form.kind === kind.value }" @click="form.kind = kind.value">
                      {{ kind.label }}
                    </button>
                  </div>
                </div>

                <div v-if="form.kind === 'Once'" class="field">
                  执行日期与时间
                  <div class="date-time-row">
                    <input name="runDate" v-model="form.runDate" class="input" type="date" required aria-label="执行日期" />
                    <div class="time-selects" role="group" aria-label="执行时间">
                      <select name="runHour" v-model.number="form.runHour" class="select" aria-label="执行小时">
                        <option v-for="hour in hourOptions" :key="hour" :value="hour">{{ pad2(hour) }}</option>
                      </select>
                      <span class="time-separator">:</span>
                      <select name="runMinute" v-model.number="form.runMinute" class="select" aria-label="执行分钟">
                        <option v-for="minute in minuteOptions" :key="minute" :value="minute">{{ pad2(minute) }}
                        </option>
                      </select>
                    </div>
                  </div>
                </div>
                <template v-else>
                  <div class="field">
                    本地执行时间
                    <div class="time-selects" role="group" aria-label="本地执行时间">
                      <select name="localHour" v-model.number="form.localHour" class="select" aria-label="执行小时">
                        <option v-for="hour in hourOptions" :key="hour" :value="hour">{{ pad2(hour) }}</option>
                      </select>
                      <span class="time-separator">:</span>
                      <select name="localMinute" v-model.number="form.localMinute" class="select" aria-label="执行分钟">
                        <option v-for="minute in minuteOptions" :key="minute" :value="minute">{{ pad2(minute) }}
                        </option>
                      </select>
                    </div>
                  </div>
                  <div class="field">
                    重复日
                    <div class="weekday-picker" role="group" aria-label="重复日">
                      <button v-for="day in weekdayOptions" :key="day.value" type="button" class="weekday"
                        :class="{ active: form.weekdays.includes(day.value) }"
                        :aria-pressed="form.weekdays.includes(day.value)" @click="toggleWeekday(day.value)">
                        {{ day.label }}
                      </button>
                    </div>
                  </div>
                </template>

                <p class="form-note">
                  覆盖 {{ selectedTargets.length }} 间教室，按服务器本地时间执行。
                  中控离线时会记为失败，不会自动补发。
                </p>
                <div class="form-actions is-start">
                  <button class="btn btn-primary" :disabled="formBusy || !selectedTargets.length">
                    {{ formBusy ? '正在保存…' : form.mode === 'edit' ? '保存' : '创建' }}
                  </button>
                  <button v-if="form.mode === 'edit'" type="button" class="btn btn-quiet" @click="closeForm">
                    取消
                  </button>
                </div>
              </div>
            </form>

            <EmptyState v-else icon="connection" title="还没有可排程的控制点" description="先在配置中为教室接入中控并添加控制点。" />
          </aside>
        </div>
      </Teleport>
    </div>

    <ConfirmDialog :open="pendingDelete !== null" title="删除自动任务" :message="pendingDelete
      ? `删除「${pendingDelete.name}」将移除 ${pendingDelete.roomNames.length} 间教室的目标与全部执行记录，此操作不可撤销。`
      : ''" confirm-label="确认删除" tone="danger" @cancel="pendingDelete = null" @confirm="confirmDelete" />
    <span v-if="deleteBusy" class="sr-only" role="status">正在删除任务</span>
  </div>
</template>
