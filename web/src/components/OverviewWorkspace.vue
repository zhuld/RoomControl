<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { request, toErrorMessage } from '../api'
import { useConfirmDialog } from '../lib/confirm'
import { formatClock, formatRelative } from '../lib/format'
import {
  buildingOf,
  collator,
  countRooms,
  floorOf,
  roomStatus,
  roomStatusFilters,
  roomStatusLabel,
  roomStatusTone,
  type RoomStatus,
  type StatusFilter,
} from '../lib/rooms'
import { groupSchedules, type ScheduleGroup } from '../lib/schedules'
import type {
  CurrentUser,
  ManualTask,
  ManualTaskRunResult,
  Room,
  Schedule,
  SettingsSection,
} from '../types'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import EmptyState from './ui/EmptyState.vue'
import RoomCascader from './ui/RoomCascader.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'

// 系统总览回答四个问题：哪里异常、哪些任务将执行、手动任务怎么触发、每间教室当前是否在线。
const props = defineProps<{
  rooms: Room[]
  schedules: Schedule[]
  manualTasks: ManualTask[]
  currentUser: CurrentUser | null
  now: Date
}>()

const emit = defineEmits<{
  selectRoom: [roomId: string]
  openAutomation: [target?: { roomId?: string; scheduleId?: string }]
  openSettings: [section: SettingsSection, roomId?: string]
  notify: [payload: { message: string; isError: boolean }]
  changed: []
}>()

const { confirmation, confirmAction, resolveConfirmation } = useConfirmDialog({
  title: '执行手动任务',
  actionLabel: '确认执行',
})

const stateBusyKey = ref('')
const runningManualTaskId = ref('')
const manualTaskResults = reactive<Record<string, ManualTaskRunResult>>({})

const selectedRoomFilter = ref('')
const levelFilters = ref<{ building: string; floor: string }>({ building: '', floor: '' })
const statusFilter = ref<StatusFilter>('all')

const canOperate = computed(() => props.currentUser?.role !== 'viewer')
const canManage = computed(() => props.currentUser?.role === 'admin')

const configuredRooms = computed(() => props.rooms.filter((room) => room.controller))
const onlineRooms = computed(() => configuredRooms.value.filter((room) => room.controller?.isOnline))
const offlineRooms = computed(() =>
  configuredRooms.value.filter((room) => !room.controller?.isOnline),
)
const unconfiguredRooms = computed(() => props.rooms.filter((room) => !room.controller))

const activeSchedules = computed(() => props.schedules.filter((schedule) => schedule.enabled))

const activeTaskCount = computed(
  () => new Set(activeSchedules.value.map((schedule) => schedule.groupId ?? schedule.id)).size,
)

// 多教室任务按 GroupId 合并；总览只展示已有下次执行时间的目标。
const scheduleGroups = computed(() =>
  groupSchedules(activeSchedules.value).filter(
    (group): group is ScheduleGroup & { nextRunAtUtc: string } => group.nextRunAtUtc !== null,
  ),
)

const overdueGroups = computed(() =>
  scheduleGroups.value.filter(
    (group) => new Date(group.nextRunAtUtc).getTime() < props.now.getTime() - 60_000,
  ),
)

const upcomingGroups = computed(() =>
  [...scheduleGroups.value]
    .sort(
      (left, right) => new Date(left.nextRunAtUtc).getTime() - new Date(right.nextRunAtUtc).getTime(),
    )
    .slice(0, 5),
)

function groupRoomsLabel(group: ScheduleGroup) {
  return group.roomNames.length > 1
    ? `${group.roomNames[0]} 等 ${group.roomNames.length} 间教室`
    : group.roomNames[0]
}

interface AttentionItem {
  id: string
  tone: 'danger' | 'warning'
  icon: string
  title: string
  detail: string
  actionLabel: string
  target: 'room' | 'settings' | 'automation'
  roomId: string
}

const attentionItems = computed<AttentionItem[]>(() => {
  const items: AttentionItem[] = []

  for (const room of offlineRooms.value) {
    const controller = room.controller
    if (!controller) continue
    items.push({
      id: `offline-${room.id}`,
      tone: 'danger',
      icon: 'alert',
      title: `${buildingOf(room)} · ${room.name} 中控离线`,
      detail: controller.lastError
        ? controller.lastError
        : '连接已断开，等待自动重连。',
      actionLabel: '查看教室',
      target: 'room',
      roomId: room.id,
    })
  }

  for (const room of unconfiguredRooms.value) {
    items.push({
      id: `unconfigured-${room.id}`,
      tone: 'warning',
      icon: 'connection',
      title: `${buildingOf(room)} · ${room.name} 尚未接入中控`,
      detail: canManage.value ? '补齐地址、端口与 IPID 后即可开始同步状态。' : '请联系管理员完成接入。',
      actionLabel: canManage.value ? '去接入中控' : '查看教室',
      target: canManage.value ? 'settings' : 'room',
      roomId: room.id,
    })
  }

  for (const group of overdueGroups.value) {
    items.push({
      id: `overdue-${group.targets[0].id}`,
      tone: 'danger',
      icon: 'clock',
      title: `任务「${group.name}」未按计划执行`,
      detail: `${groupRoomsLabel(group)} · 计划时间 ${formatRelative(group.nextRunAtUtc, props.now.getTime())}，请检查执行记录。`,
      actionLabel: '查看任务',
      target: 'automation',
      roomId: '',
    })
  }

  return items
})

const attentionCount = computed(() => attentionItems.value.length)

const buildingCount = computed(() => new Set(props.rooms.map(buildingOf)).size)
const floorCount = computed(
  () => new Set(props.rooms.map((room) => `${buildingOf(room)}/${floorOf(room)}`)).size,
)

const sortedRooms = computed(() => {
  const priority: Record<RoomStatus, number> = { offline: 0, unconfigured: 1, online: 2 }
  return [...props.rooms].sort(
    (left, right) =>
      priority[roomStatus(left)] - priority[roomStatus(right)] ||
      collator.compare(buildingOf(left), buildingOf(right)) ||
      collator.compare(floorOf(left), floorOf(right)) ||
      collator.compare(left.name, right.name),
  )
})

// 三级选择同时决定楼栋/楼层范围与单间教室范围，状态筛选只在此基础上收窄。
const locationRooms = computed(() => {
  const { building, floor } = levelFilters.value
  return sortedRooms.value.filter((room) => {
    if (building && buildingOf(room) !== building) return false
    if (floor && floorOf(room) !== floor) return false
    if (selectedRoomFilter.value && room.id !== selectedRoomFilter.value) return false
    return true
  })
})

const filteredRooms = computed(() =>
  locationRooms.value.filter(
    (room) => statusFilter.value === 'all' || roomStatus(room) === statusFilter.value,
  ),
)

function filterCount(status: StatusFilter) {
  return countRooms(locationRooms.value, status)
}

function handleLevelsChange(levels: { building: string; floor: string }) {
  levelFilters.value = levels
}

watch(
  () => props.rooms,
  (rooms) => {
    const { building, floor } = levelFilters.value
    if (selectedRoomFilter.value && !rooms.some((room) => room.id === selectedRoomFilter.value)) {
      selectedRoomFilter.value = ''
    }
    if (building && !rooms.some((room) => buildingOf(room) === building)) {
      levelFilters.value = { building: '', floor: '' }
    } else if (
      floor &&
      !rooms.some((room) => buildingOf(room) === building && floorOf(room) === floor)
    ) {
      levelFilters.value = { building, floor: '' }
    }
  },
)

function handleAttention(item: AttentionItem) {
  if (item.target === 'room') {
    if (!canOperate.value) {
      document
        .getElementById(`overview-room-${item.roomId}`)
        ?.scrollIntoView({ behavior: 'smooth', block: 'center' })
      return
    }
    emit('selectRoom', item.roomId)
    return
  }
  if (item.target === 'automation') {
    emit('openAutomation', { scheduleId: item.id.replace('overdue-', '') })
    return
  }
  emit('openSettings', 'controllers', item.roomId)
}

function manualTaskRoomsLabel(task: ManualTask) {
  const names = task.targets.map((target) => target.roomName)
  if (names.length <= 2) return names.join('、')
  return `${names.slice(0, 2).join('、')} 等 ${names.length} 间`
}


function manualTaskLabel(task: ManualTask) {
  if (runningManualTaskId.value === task.id) return '正在下发…'
  const result = manualTaskResults[task.id]
  if (!result) return '点击执行'
  return `成功 ${result.succeededCount} / ${result.succeededCount + result.failedCount}`
}

// 总览按钮直接下发全部目标；任务可配置是否先弹出二次确认。
async function runManualTask(task: ManualTask) {
  if (runningManualTaskId.value) return
  if (!task.targets.length) {
    emit('notify', { message: `任务「${task.name}」还没有可执行的控制点。`, isError: true })
    return
  }

  if (task.confirmBeforeRun) {
    const confirmed = await confirmAction(
      `将向 ${task.targets.length} 间教室发送「${task.joinName}」指令，确定执行「${task.name}」吗？`,
    )
    if (!confirmed) return
  }

  runningManualTaskId.value = task.id
  try {
    const response = await request(`/api/manual-tasks/${task.id}/run`, { method: 'POST' })
    const result = (await response.json()) as ManualTaskRunResult
    manualTaskResults[task.id] = result
    if (result.failedCount > 0) {
      const failures = result.targets
        .filter((target) => target.status === 'Failed')
        .slice(0, 3)
        .map((target) => `${target.roomName}：${target.error ?? '发送失败'}`)
        .join('；')
      emit('notify', {
        message: `「${result.taskName}」成功 ${result.succeededCount} 间，失败 ${result.failedCount} 间。${failures}`,
        isError: true,
      })
    } else {
      emit('notify', {
        message: `「${result.taskName}」已发送至 ${result.succeededCount} 间教室。`,
        isError: false,
      })
    }
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    runningManualTaskId.value = ''
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
</script>

<template>
  <div class="workspace-inner">

    <section v-if="canOperate" class="kpi-grid overview-summary" aria-label="站点概况">
      <article class="kpi is-accent">
        <span class="kpi-icon">
          <UiIcon name="room" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">教室</span>
          <span class="kpi-detail">{{ buildingCount }} 栋 · {{ floorCount }} 个楼层</span>
        </div>
        <strong class="kpi-value">{{ rooms.length }}</strong>
      </article>
      <article class="kpi" :class="onlineRooms.length === configuredRooms.length ? 'is-success' : 'is-warning'">
        <span class="kpi-icon">
          <UiIcon name="connection" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">在线中控</span>
          <span class="kpi-detail">{{ offlineRooms.length }} 离线 · {{ unconfiguredRooms.length }} 未接入</span>
        </div>
        <strong class="kpi-value">{{ onlineRooms.length }}<span class="text-sm muted"> / {{ configuredRooms.length
            }}</span></strong>
      </article>
      <article class="kpi" :class="attentionCount ? 'is-danger' : 'is-success'">
        <span class="kpi-icon">
          <UiIcon name="alert" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">需要处理</span>
          <span class="kpi-detail">
            {{ !rooms.length ? '先完成教室接入' : attentionCount ? '按严重程度排序' : '当前没有待处理项' }}
          </span>
        </div>
        <strong class="kpi-value">{{ attentionCount }}</strong>
      </article>
      <article class="kpi is-info">
        <span class="kpi-icon">
          <UiIcon name="automation" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">启用任务</span>
          <span class="kpi-detail">
            {{ upcomingGroups[0] ? `下次 ${formatRelative(upcomingGroups[0].nextRunAtUtc, now.getTime())}` :
              '暂无计划执行' }}
          </span>
        </div>
        <strong class="kpi-value">{{ activeTaskCount }}</strong>
      </article>
    </section>

    <div v-if="canOperate" class="overview-grid">
      <section class="panel" aria-label="需要处理">
        <header class="panel-head">
          <div class="panel-head-copy">
            <h2>需要处理</h2>
            <p class="panel-sub">离线、未接入与逾期任务按风险排序，处理完成后自动移出。</p>
          </div>
          <StatusPill :tone="attentionCount ? 'danger' : 'success'"
            :label="attentionCount ? `${attentionCount} 项` : '全部正常'" dot />
        </header>

        <EmptyState v-if="!rooms.length" icon="room" title="还没有教室" description="先完成楼栋、教室与中控接入，系统总览才会显示可信的教室状态。">
          <button v-if="canManage" type="button" class="btn btn-primary btn-sm empty-action"
            @click="emit('openSettings', 'locations')">
            开始配置
          </button>
        </EmptyState>
        <div v-else-if="attentionItems.length" class="attention-list">
          <article v-for="item in attentionItems" :key="item.id" class="attention-item" :class="`is-${item.tone}`">
            <span class="attention-icon">
              <UiIcon :name="item.icon" :size="15" />
            </span>
            <div class="attention-copy">
              <strong>{{ item.title }}</strong>
              <span :title="item.detail">{{ item.detail }}</span>
            </div>
            <button type="button" class="btn btn-quiet btn-sm" @click="handleAttention(item)">
              {{ item.actionLabel }}
            </button>
          </article>
        </div>
        <div v-else class="panel-body">
          <div class="banner is-success">
            <UiIcon name="check" :size="16" />
            <div>
              <strong>所有已接入中控的教室均在线</strong>
              <p>实时状态同步正常，没有逾期未执行的自动任务。</p>
            </div>
          </div>
        </div>
      </section>

      <section class="panel" aria-label="即将自动执行">
        <header class="panel-head">
          <div class="panel-head-copy">
            <h2>即将自动执行</h2>
            <p class="panel-sub">按计划时间排序，仅显示已启用任务。</p>
          </div>
          <button v-if="canOperate" type="button" class="btn btn-ghost btn-sm" @click="emit('openAutomation')">
            全部任务
            <UiIcon name="chevron-right" :size="14" />
          </button>
        </header>

        <div v-if="upcomingGroups.length" class="attention-list">
          <article v-for="group in upcomingGroups" :key="group.key" class="attention-item">
            <span class="attention-icon">
              <UiIcon name="clock" :size="15" />
            </span>
            <div class="attention-copy">
              <strong>{{ group.name }}</strong>
              <span>{{ groupRoomsLabel(group) }} · {{ group.joinName }}</span>
            </div>
                <button v-if="canOperate" type="button" class="btn btn-quiet btn-sm"
                  :disabled="stateBusyKey === group.key" :title="group.enabled ? '暂停后不再按计划执行，可随时恢复' : '按当前时间重新排定执行'"
                  @click="toggleTaskState(group)">
                  {{ group.enabled ? '暂停' : '恢复' }}
                </button>
            <StatusPill :tone="overdueGroups.some((item) => item.key === group.key) ? 'danger' : 'neutral'"
              :label="formatRelative(group.nextRunAtUtc, now.getTime())" />
          </article>
        </div>
        <EmptyState v-else icon="calendar" title="没有已启用的自动任务"
          :description="canOperate ? '进入配置与接入 · 自动任务，为教室控制点安排一次性或每周计划。' : '管理员尚未安排自动任务。'">
          <button v-if="canOperate" type="button" class="btn btn-outline btn-sm empty-action"
            @click="emit('openAutomation')">
            创建任务
          </button>
        </EmptyState>
      </section>
    </div>

    <section v-if="canOperate" class="panel section-gap" aria-label="手动任务">
      <header class="panel-head">
        <div class="panel-head-copy">
          <h2>手动任务</h2>
          <p class="panel-sub">每个按钮对应多间教室的一个控制点，点击后同时下发。</p>
        </div>
        <button type="button" class="btn btn-ghost btn-sm" @click="emit('openSettings', 'manual-tasks')">
          管理任务
          <UiIcon name="chevron-right" :size="14" />
        </button>
      </header>

      <div v-if="manualTasks.length" class="panel-body">
        <div class="manual-task-grid">
          <button v-for="task in manualTasks" :key="task.id" type="button" class="manual-task-button"
            :disabled="runningManualTaskId !== '' || !task.targets.length"
            :title="task.targets.length ? `${task.joinName} · ${manualTaskRoomsLabel(task)}` : '还没有可执行的控制点'"
            @click="runManualTask(task)">
            <span class="manual-task-name">{{ task.name }}</span>
            <span class="manual-task-meta">{{ task.joinName }} · {{ manualTaskRoomsLabel(task) }}</span>
            <span class="manual-task-foot">
              <span class="text-2xs muted tabular">{{ manualTaskLabel(task) }}</span>
              <span v-if="manualTaskResults[task.id]" class="text-2xs muted tabular">
                {{ formatClock(manualTaskResults[task.id].completedAtUtc) }}
              </span>
            </span>
          </button>
        </div>
      </div>
      <EmptyState v-else icon="bolt" title="还没有手动任务" description="进入配置与接入 · 手动任务，把多间教室的同名控制点聚合为一个总览按钮。">
        <button type="button" class="btn btn-outline btn-sm empty-action" @click="emit('openSettings', 'manual-tasks')">
          创建手动任务
        </button>
      </EmptyState>
    </section>

    <section class="panel section-gap" aria-label="教室状态">
      <header class="panel-toolbar">
        <div class="room-toolbar-left">
          <RoomCascader v-model="selectedRoomFilter" :rooms="rooms" allow-all class="room-toolbar-cascader"
            @levels-change="handleLevelsChange" />
          <div class="chip-row" role="group" aria-label="按状态筛选">
            <button v-for="option in roomStatusFilters" :key="option.value" type="button" class="chip"
              :class="{ active: statusFilter === option.value }" :aria-pressed="statusFilter === option.value"
              @click="statusFilter = option.value">
              {{ option.label }}<span class="chip-count">{{ filterCount(option.value) }}</span>
            </button>
          </div>
        </div>
        <span class="text-xs muted">{{ filteredRooms.length }} / {{ rooms.length }} 间</span>
      </header>

      <div v-if="filteredRooms.length" class="panel-body">
        <div class="room-grid">
          <button v-for="room in filteredRooms" :id="`overview-room-${room.id}`" :key="room.id" type="button"
            class="room-card" :class="`is-${roomStatus(room)}`" :disabled="!canOperate"
            @click="emit('selectRoom', room.id)"
            :title="room.controller ? `${room.controller.name}\n\r${room.controller.host}:${room.controller.port} · IPID ${room.controller.ipid}\n\r${buildingOf(room)} · ${floorOf(room)} · ${room.name}` : `${buildingOf(room)} · ${floorOf(room)} · ${room.name} · 尚未接入中控`">

            <div class="room-card-head">
              <div class="room-card-title">
                <p class="eyebrow">{{ buildingOf(room) }} · {{ floorOf(room) }}</p>
                <h3>{{ room.name }}</h3>
              </div>
              <StatusPill :tone="roomStatusTone(room)" :label="roomStatusLabel(room)" dot />
            </div>
            <div v-if="room.controller?.joins.length" class="room-join-list">
              <span v-for="join in room.controller.joins" :key="join.id" class="room-join"
                :class="{ 'is-on': join.stateSnapshot.digitalValue }">
                {{ join.name }}
              </span>
            </div>
          </button>
        </div>
      </div>
      <div v-else class="panel-body">
        <EmptyState v-if="!rooms.length" icon="room" title="还没有教室" description="先配置楼栋与楼层，再创建教室并接入中控。">
          <button v-if="canManage" type="button" class="btn btn-primary btn-sm empty-action"
            @click="emit('openSettings', 'locations')">
            开始配置
          </button>
        </EmptyState>
        <EmptyState v-else icon="search" title="没有符合条件的教室" description="调整搜索词或状态筛选后再试。" />
      </div>
    </section>

    <ConfirmDialog :open="confirmation !== null" :title="confirmation?.title ?? ''"
      :message="confirmation?.message ?? ''" :confirm-label="confirmation?.actionLabel ?? '确认'"
      :tone="confirmation?.tone ?? 'default'" @cancel="resolveConfirmation(false)"
      @confirm="resolveConfirmation(true)" />
  </div>
</template>
