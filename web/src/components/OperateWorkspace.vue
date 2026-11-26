<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatClock, formatRelative } from '../lib/format'
import {
  buildingOf,
  collator,
  floorOf,
  groupRoomsByBuilding,
  roomStatus,
  type StatusFilter,
} from '../lib/rooms'
import { receiptStatuses } from '../types'
import type {
  CommandReceipt,
  CurrentUser,
  FeedbackTone,
  JoinMapping,
  ReceiptStatus,
  Room,
  Schedule,
  SettingsSection,
} from '../types'
import EmptyState from './ui/EmptyState.vue'
import RoomCascader from './ui/RoomCascader.vue'
import RoomTree from './ui/RoomTree.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'

// 教室操作按「定位 → 确认状态 → 下发 → 查看回执」组织，不隐藏任何失效原因。
const props = defineProps<{
  rooms: Room[]
  selectedRoomId: string
  currentUser: CurrentUser | null
  hubState: 'connecting' | 'connected' | 'disconnected'
  lastHubEventAt: string | null
  receipts: CommandReceipt[]
  schedules: Schedule[]
  now: Date
}>()

const emit = defineEmits<{
  selectRoom: [roomId: string]
  sendCommand: [room: Room, join: JoinMapping]
  scheduleCommand: [room: Room, join: JoinMapping]
  openSettings: [section: SettingsSection, roomId: string]
  clearReceipts: []
}>()

const statusFilter = ref<StatusFilter>('all')
// 回执列表默认收起，避免长期占用操作区；顶部按钮随时展开。
const receiptRailOpen = ref(false)

const canOperate = computed(() => props.currentUser?.role !== 'viewer')
const canManage = computed(() => props.currentUser?.role === 'admin')
const failedReceiptCount = computed(
  () => props.receipts.filter((receipt) => receipt.status === 'failed').length,
)

const selectedRoom = computed(
  () => props.rooms.find((room) => room.id === props.selectedRoomId) ?? null,
)
const controller = computed(() => selectedRoom.value?.controller ?? null)
const joins = computed(() => controller.value?.joins ?? [])

function filteredRoom(room: Room) {
  return statusFilter.value === 'all' || roomStatus(room) === statusFilter.value
}

function selectRoomFromCascader(roomId: string) {
  if (roomId) emit('selectRoom', roomId)
}

const roomGroups = computed(() => {
  const visibleRooms = props.rooms
    .filter(filteredRoom)
    .sort((left, right) => collator.compare(left.name, right.name))
  return groupRoomsByBuilding(visibleRooms)
})

function joinOnCount(room: Room) {
  return room.controller?.joins.filter((join) => join.stateSnapshot.digitalValue).length ?? 0
}

function joinTotal(room: Room) {
  return room.controller?.joins.length ?? 0
}

const latestReceipts = computed(() => {
  const map = new Map<string, CommandReceipt>()
  for (const receipt of props.receipts) {
    if (!map.has(receipt.joinId)) map.set(receipt.joinId, receipt)
  }
  return map
})

function receiptFor(join: JoinMapping) {
  return latestReceipts.value.get(join.id) ?? null
}

function isSending(join: JoinMapping) {
  return receiptFor(join)?.status === 'sending'
}

// 每个控制点只关心最近一次待执行的计划，倒计时随页面时钟刷新。
const joinPlans = computed(() => {
  const plans = new Map<string, { name: string; countdown: string }>()
  const nextRun = new Map<string, number>()
  for (const schedule of props.schedules) {
    const joinId = schedule.joinMapping?.id
    if (!joinId || !schedule.enabled || !schedule.nextRunAtUtc) continue
    const runAt = new Date(schedule.nextRunAtUtc).getTime()
    if (Number.isNaN(runAt)) continue
    const current = nextRun.get(joinId)
    if (current !== undefined && current <= runAt) continue
    nextRun.set(joinId, runAt)
    plans.set(joinId, {
      name: schedule.name,
      countdown: runAt <= props.now.getTime() ? '已到执行时间' : formatRelative(schedule.nextRunAtUtc, props.now.getTime()),
    })
  }
  return plans
})

function planFor(join: JoinMapping) {
  return joinPlans.value.get(join.id) ?? null
}

// 不可操作时给出唯一、直接的原因，避免用户猜测按钮为什么无效。
function gatingReason(join: JoinMapping): string | null {
  if (!controller.value) return '未接入中控'
  if (!controller.value.enabled) return '中控连接已停用'
  if (!controller.value.isOnline) return '中控离线'
  if (!canOperate.value) return '当前账号只读'
  if (join.enableJoinNumber > 0 && join.enableSnapshot?.digitalValue !== true) return '使能信号未就绪'
  return null
}

function commandDisabled(join: JoinMapping) {
  return gatingReason(join) !== null || isSending(join)
}

const hubBanner = computed(() => {
  if (props.hubState === 'disconnected') {
    return {
      tone: 'danger' as FeedbackTone,
      title: '实时同步已断开，当前状态可能滞后',
      detail: props.lastHubEventAt
        ? `最后一次实时事件在 ${formatRelative(props.lastHubEventAt, props.now.getTime())}。可刷新快照，或检查网络后等待重连。`
        : '尚未收到任何实时事件。可刷新快照获取最新状态。',
    }
  }
  if (props.hubState === 'connecting') {
    return {
      tone: 'info' as FeedbackTone,
      title: '正在建立实时连接',
      detail: '连接完成前，控制点状态可能延迟更新。',
    }
  }
  return null
})

// 回执状态的唯一定义来源：文案与色调都从这里取。
const receiptMeta = (status: ReceiptStatus) =>
  receiptStatuses.find((item) => item.value === status) ?? receiptStatuses[0]

function receiptTone(status: CommandReceipt['status']): FeedbackTone {
  return receiptMeta(status).tone
}

function receiptLabel(status: CommandReceipt['status']) {
  return receiptMeta(status).label
}
</script>

<template>
  <div class="workspace-inner">
    <div class="operate-layout" :class="{ 'is-rail-open': receiptRailOpen }">
      <aside v-if="rooms.length" class="panel room-finder" aria-label="选择教室">
        <strong class="panel-head">选择教室</strong>
        <div class="room-finder-head">
          <RoomCascader stacked :rooms="rooms" :model-value="selectedRoomId"
            @update:model-value="selectRoomFromCascader" />
        </div>

        <RoomTree :groups="roomGroups" :selected-id="selectedRoomId" label="教室列表" @select="emit('selectRoom', $event)">
          <template #status="{ room }">
            <span class="dot" :class="{
              'is-online': roomStatus(room) === 'online',
              'is-offline': roomStatus(room) === 'offline',
              'is-unknown': roomStatus(room) === 'unconfigured',
            }"></span>
          </template>
          <template #meta="{ room }">{{ joinOnCount(room) }}/{{ joinTotal(room) }}</template>
        </RoomTree>
      </aside>

      <main class="operate-main">
        <EmptyState v-if="!rooms.length" icon="room" title="还没有教室" description="先由管理员创建教室并接入中控，之后才能下发指令。">
          <button v-if="canManage" type="button" class="btn btn-primary empty-action"
            @click="emit('openSettings', 'rooms', '')">
            前往配置
          </button>
        </EmptyState>

        <EmptyState v-else-if="!selectedRoom" icon="room" title="选择一间教室开始操作" description="从左侧列表按楼栋或搜索定位教室。" />

        <template v-else>
          <section class="room-hero" aria-label="教室状态">
            <div class="room-hero-title">
              <p class="eyebrow room-hero-location">
                {{ buildingOf(selectedRoom) }} / {{ floorOf(selectedRoom) }}
              </p>
              <h1>{{ selectedRoom.name }}</h1>
              <div class="room-hero-meta">
                <span class="meta-item">
                  <StatusPill :tone="controller ? (controller.isOnline ? 'success' : 'danger') : 'warning'"
                    :label="controller ? (controller.isOnline ? '中控在线' : '中控离线') : '未接入中控'" dot
                    :pulse="controller?.isOnline" />
                </span>
                <template v-if="controller">
                  <span class="meta-item mono">{{ controller.name }}</span>
                  <span class="meta-item mono">{{ controller.host }}:{{ controller.port }}</span>
                  <span class="meta-item mono">IPID {{ controller.ipid }}</span>
                </template>
                <span v-else class="meta-item">状态不可知，等待接入中控</span>
              </div>
            </div>
            <div class="room-hero-actions">
              <StatusPill v-if="hubState === 'connected'" tone="success"
                :label="lastHubEventAt ? `实时事件 ${formatRelative(lastHubEventAt, now.getTime())}` : '实时同步正常'" dot />
              <StatusPill v-else-if="hubState === 'connecting'" tone="warning" label="实时连接中" dot pulse />
              <StatusPill v-else tone="danger" label="实时同步断开" dot />
              <button v-if="canManage" type="button" class="btn btn-outline btn-sm"
                @click="emit('openSettings', 'controllers', selectedRoom.id)">
                <UiIcon name="settings" :size="15" />中控配置
              </button>
              <button type="button" class="btn btn-quiet" :class="{ 'is-active': receiptRailOpen }"
                :aria-expanded="receiptRailOpen" aria-controls="operate-receipts"
                :title="receiptRailOpen ? '收起操作回执' : '展开操作回执'" @click="receiptRailOpen = !receiptRailOpen">
                <UiIcon name="inbox" :size="16" />操作回执
                <span v-if="receipts.length" class="btn-count" :class="{ 'is-danger': failedReceiptCount > 0 }">{{
                  receipts.length
                }}</span>
              </button>
            </div>
          </section>

          <div v-if="hubBanner" class="banner" :class="`is-${hubBanner.tone}`" role="status">
            <UiIcon :name="hubBanner.tone === 'danger' ? 'alert' : 'connection'" :size="16" />
            <div>
              <strong>{{ hubBanner.title }}</strong>
              <p>{{ hubBanner.detail }}</p>
            </div>
          </div>

          <div v-if="!controller" class="banner is-warning" role="status">
            <UiIcon name="connection" :size="16" />
            <div>
              <strong>这间教室尚未接入中控</strong>
              <p>
                {{ canManage ? '配置地址、端口与 IPID 后，后台会自动连接并同步 Join 状态。' : '请联系管理员完成中控接入。' }}
              </p>
            </div>
            <button v-if="canManage" type="button" class="btn btn-outline btn-sm"
              @click="emit('openSettings', 'controllers', selectedRoom.id)">
              去接入
            </button>
          </div>

          <div v-else-if="!controller.isOnline" class="banner is-danger" role="alert">
            <UiIcon name="alert" :size="16" />
            <div>
              <strong>中控离线，指令无法下发</strong>
              <p>
                <template v-if="controller.lastError">连接错误：{{ controller.lastError }}</template>
                <template v-else>请检查电源、网络与连接配置后刷新快照。</template>
              </p>
            </div>
          </div>

          <section class="panel" aria-label="设备控制">
            <div v-if="joins.length" class="panel-body control-stage">
              <!-- 视频窗口预留位：接入教室摄像头后在此替换为实时画面。 -->
              <figure class="control-video" aria-label="教室视频画面">
                <div class="control-video-frame">
                  <span class="control-video-caption">{{ selectedRoom.name }}</span>
                  <span class="control-video-empty">
                    <UiIcon name="video" :size="26" />
                    <strong>视频窗口</strong>
                    <span>预留画面区域，接入教室摄像头后在此显示</span>
                  </span>
                </div>
              </figure>

              <!-- 控制点按后端排序（SortOrder）自上而下排列在视频窗口右侧，与视频同高并独立滚动。 -->
              <div class="control-buttons">
                <div class="command-list">
                  <article v-for="join in joins" :key="join.id" class="command-card" :class="{
                    'is-on': join.stateSnapshot.digitalValue,
                    'is-disabled': gatingReason(join) !== null,
                    'is-sending': isSending(join),
                  }">
                    <button type="button" class="command-main" :disabled="commandDisabled(join)"
                      :title="gatingReason(join) ?? `发送「${join.name}」指令\n发送数字Join： ${join.sendJoinNumber}\n状态数字Join： ${join.stateJoinNumber}\n使能数字Join： ${join.enableJoinNumber}`"
                      @click="emit('sendCommand', selectedRoom, join)">
                      <div class="command-top">
                        <span class="command-name">{{ join.name }}</span>
                        <span class="command-top-right">
                          <span v-if="join.showConfirmationPopup" class="command-guard" title="下发前需二次确认"
                            aria-label="下发前需二次确认">
                            <UiIcon name="alert" :size="13" />
                          </span>
                        </span>
                      </div>
                    </button>
                    <footer class="command-foot">
                      <span v-if="planFor(join)" class="command-plan"
                        :title="`${planFor(join)?.name} · ${planFor(join)?.countdown}`">
                        <span class="command-plan-time">{{ planFor(join)?.countdown }}</span>
                        <span class="command-plan-name">{{ planFor(join)?.name }}</span>
                      </span>
                      <span v-else class="command-plan is-empty">无自动任务</span>
                      <span v-if="planFor(join)" class="command-schedule">
                        <UiIcon name="clock" :size="12" />
                      </span>
                    </footer>
                  </article>
                </div>
              </div>
            </div>

            <EmptyState v-else icon="bolt" title="这间教室还没有控制点"
              :description="controller ? '在中控配置中添加发送、状态与使能 Join 映射后，即可在此下发指令。' : '先完成中控接入，再添加控制点。'">
              <button v-if="canManage" type="button" class="btn btn-primary empty-action"
                @click="emit('openSettings', 'controllers', selectedRoom.id)">
                添加控制点
              </button>
            </EmptyState>
          </section>
        </template>
      </main>

      <aside v-if="receiptRailOpen" id="operate-receipts" class="panel receipt-rail" aria-label="操作回执">
        <header class="panel-head">
          <div class="panel-head-copy">
            <p class="eyebrow">可追踪反馈</p>
            <h2>操作回执</h2>
          </div>
          <div class="receipt-rail-actions">
            <button v-if="receipts.length" type="button" class="btn btn-ghost btn-sm" title="清空当前会话回执"
              @click="emit('clearReceipts')">
              清空
            </button>
            <button type="button" class="btn btn-ghost btn-sm receipt-rail-close" title="收起操作回执" aria-label="收起操作回执"
              @click="receiptRailOpen = false">
              <UiIcon name="x" :size="14" />
            </button>
          </div>
        </header>

        <div v-if="receipts.length" class="receipt-list">
          <article v-for="receipt in receipts" :key="receipt.id" class="receipt">
            <span class="dot receipt-dot" :class="`is-${receiptTone(receipt.status)}`"></span>
            <div class="receipt-copy">
              <strong>{{ receipt.joinName }}</strong>
              <span>{{ receipt.roomName }}</span>
              <div class="receipt-meta">
                <StatusPill :tone="receiptTone(receipt.status)" :label="receiptLabel(receipt.status)" />
                <span class="tabular">{{ formatClock(receipt.at) }}</span>
              </div>
              <p v-if="receipt.message" class="receipt-error">{{ receipt.message }}</p>
            </div>
          </article>
        </div>
        <EmptyState v-else icon="inbox" title="本会话尚无回执" description="发送指令后，这里会显示发送中、已发送或失败原因。" />

        <footer class="receipt-rail-foot">
          本次浏览会话内的下发结果，按时间倒序，用于即时核对。服务端审计记录可在「操作记录」中查询。
        </footer>
      </aside>
    </div>
  </div>
</template>
