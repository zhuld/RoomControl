<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { formatRelative } from '../lib/format'
import {
  buildingOf,
  collator,
  countRooms,
  floorOf,
  roomStatus,
  roomStatusLabel,
  roomStatusTone,
  type RoomStatus,
  type StatusFilter,
} from '../lib/rooms'
import { groupSchedules, type ScheduleGroup } from '../lib/schedules'
import type { CurrentUser, Room, Schedule, SettingsSection } from '../types'
import EmptyState from './ui/EmptyState.vue'
import RoomCascader from './ui/RoomCascader.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'

// 系统总览只回答三个问题：哪里异常、哪些任务将执行、每间教室当前是否在线。
const props = defineProps<{
  rooms: Room[]
  schedules: Schedule[]
  currentUser: CurrentUser | null
  now: Date
}>()

const emit = defineEmits<{
  selectRoom: [roomId: string]
  openAutomation: [target?: { roomId?: string; scheduleId?: string }]
  openSettings: [section: SettingsSection, roomId?: string]
}>()

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
</script>

<template>
  <div class="workspace-inner">
    <header class="workspace-header">
      <div class="workspace-header-copy">
        <p class="eyebrow">值守 · 接班</p>
        <h1>系统总览</h1>
        <p v-if="canOperate">先处理异常，再进入教室操作。控制点状态以中控回报为准，尚未回报时按低电平（关闭）显示。</p>
        <p v-else>只读查看各教室的中控在线状态与控制点状态；尚未回报的控制点按低电平（关闭）显示。</p>
      </div>
    </header>

    <section class="kpi-grid overview-summary" aria-label="站点概况">
      <article class="kpi is-accent">
        <span class="kpi-icon">
          <UiIcon name="room" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">教室</span>
          <strong class="kpi-value">{{ rooms.length }}</strong>
          <span class="kpi-detail">{{ buildingCount }} 栋 · {{ floorCount }} 个楼层</span>
        </div>
      </article>
      <article class="kpi" :class="onlineRooms.length === configuredRooms.length ? 'is-success' : 'is-warning'">
        <span class="kpi-icon">
          <UiIcon name="connection" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">在线中控</span>
          <strong class="kpi-value">{{ onlineRooms.length }}<span class="text-sm muted"> / {{ configuredRooms.length
              }}</span></strong>
          <span class="kpi-detail">{{ offlineRooms.length }} 离线 · {{ unconfiguredRooms.length }} 未接入</span>
        </div>
      </article>
      <article v-if="canOperate" class="kpi" :class="attentionCount ? 'is-danger' : 'is-success'">
        <span class="kpi-icon">
          <UiIcon name="alert" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">需要处理</span>
          <strong class="kpi-value">{{ attentionCount }}</strong>
          <span class="kpi-detail">
            {{ !rooms.length ? '先完成教室接入' : attentionCount ? '按严重程度排序' : '当前没有待处理项' }}
          </span>
        </div>
      </article>
      <article class="kpi is-info">
        <span class="kpi-icon">
          <UiIcon name="automation" :size="17" />
        </span>
        <div class="kpi-copy">
          <span class="kpi-label">启用任务</span>
          <strong class="kpi-value">{{ activeTaskCount }}</strong>
          <span class="kpi-detail">
            {{ upcomingGroups[0] ? `下次 ${formatRelative(upcomingGroups[0].nextRunAtUtc, now.getTime())}` :
            '暂无计划执行' }}
          </span>
        </div>
      </article>
    </section>

    <div v-if="canOperate" class="overview-grid">
      <section class="panel" aria-label="需要处理">
        <header class="panel-head">
          <div class="panel-head-copy">
            <p class="eyebrow">异常优先</p>
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
            <p class="eyebrow">计划</p>
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
            <StatusPill
              :tone="overdueGroups.some((item) => item.key === group.key) ? 'danger' : 'neutral'"
              :label="formatRelative(group.nextRunAtUtc, now.getTime())"
            />
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

    <section class="panel section-gap" aria-label="教室状态">
      <header class="panel-toolbar">
        <div class="room-toolbar-left">
          <RoomCascader v-model="selectedRoomFilter" :rooms="rooms" allow-all class="room-toolbar-cascader"
            @levels-change="handleLevelsChange" />
          <div class="chip-row" role="group" aria-label="按状态筛选">
            <button v-for="option in ([
              { value: 'all', label: '全部' },
              { value: 'online', label: '在线' },
              { value: 'offline', label: '离线' },
              { value: 'unconfigured', label: '未接入' },
            ] as const)" :key="option.value" type="button" class="chip"
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
            @click="emit('selectRoom', room.id)">
            <div class="room-card-head">
              <div class="room-card-title">
                <p class="eyebrow">{{ buildingOf(room) }} · {{ floorOf(room) }}</p>
                <h3>{{ room.name }}</h3>
              </div>
              <StatusPill :tone="roomStatusTone(room)" :label="roomStatusLabel(room)" dot />
            </div>
            <p class="room-card-meta">
              <template v-if="room.controller">
                {{ room.controller.name }} · {{ room.controller.host }}:{{ room.controller.port }} · IPID {{
                  room.controller.ipid }}
              </template>
              <template v-else>尚未接入中控，状态不可知</template>
            </p>
            <div v-if=" room.controller?.joins.length" class="room-join-list">
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
  </div>
</template>
