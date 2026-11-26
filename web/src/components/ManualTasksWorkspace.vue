<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { readJson, request, toErrorMessage } from '../api'
import { formatDateTime } from '../lib/format'
import { collator } from '../lib/rooms'
import type {
  CurrentUser,
  JoinMapping,
  ManualTask,
  ManualTaskExecution,
  Room,
} from '../types'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import EmptyState from './ui/EmptyState.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'

// 手动任务面板：配置总览按钮覆盖的教室与控制点，并查看每次批量下发的结果。
const props = defineProps<{
  rooms: Room[]
  manualTasks: ManualTask[]
  currentUser: CurrentUser | null
  selectedRoomId: string
}>()

const emit = defineEmits<{
  changed: []
  notify: [payload: { message: string; isError: boolean }]
}>()

const taskQuery = ref('')
const roomFilter = ref('')
const openTaskId = ref('')
const historyCache = reactive<Record<string, ManualTaskExecution[]>>({})
const historyLoadingId = ref('')
const historyError = ref('')

const formOpen = ref(false)
const formBusy = ref(false)
const pendingDelete = ref<ManualTask | null>(null)
const deleteBusy = ref(false)

const form = reactive({
  mode: 'create' as 'create' | 'edit',
  editingId: '',
  roomIds: [] as string[],
  joinName: '',
  name: '',
  confirmBeforeRun: true,
})

const canOperate = computed(() => props.currentUser?.role !== 'viewer')

const schedulableRooms = computed(() =>
  props.rooms.filter((room) => room.controller && room.controller.joins.length > 0),
)

const roomOptions = computed(() =>
  [...new Set(props.manualTasks.flatMap((task) => task.targets.map((target) => target.roomName)))]
    .sort(collator.compare),
)

const filteredTasks = computed(() => {
  const query = taskQuery.value.trim().toLocaleLowerCase()
  return props.manualTasks.filter((task) => {
    if (roomFilter.value && !task.targets.some((target) => target.roomName === roomFilter.value)) {
      return false
    }
    if (!query) return true
    return `${task.name} ${task.joinName} ${task.targets.map((target) => target.roomName).join(' ')}`
      .toLocaleLowerCase()
      .includes(query)
  })
})

const selectedRooms = computed(() =>
  schedulableRooms.value.filter((room) => form.roomIds.includes(room.id)),
)

// 控制点按名称对齐：只有所选教室都具备的名称才能作为一个手动任务的目标。
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

function taskRoomsLabel(task: ManualTask) {
  const names = task.targets.map((target) => target.roomName)
  if (names.length <= 2) return names.join('、')
  return `${names.slice(0, 2).join('、')} 等 ${names.length} 间`
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

watch(schedulableRooms, () => ensureRoomSelection(), { immediate: true })

watch(
  commonJoinNames,
  (names) => {
    if (!names.includes(form.joinName)) form.joinName = names[0] ?? ''
  },
  { immediate: true },
)

function resetForm() {
  form.mode = 'create'
  form.editingId = ''
  form.name = ''
  form.confirmBeforeRun = true
  form.joinName = commonJoinNames.value[0] ?? ''
}

function openCreate() {
  resetForm()
  formOpen.value = true
  void nextTick(() => document.getElementById('manual-form')?.focus())
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

  const body = {
    joinMappingIds: selectedTargets.value.map((join) => join.id),
    name: form.name.trim(),
    confirmBeforeRun: form.confirmBeforeRun,
  }
  const editing = form.mode === 'edit' && form.editingId
  formBusy.value = true
  try {
    await request(editing ? `/api/manual-tasks/${form.editingId}` : '/api/manual-tasks', {
      method: editing ? 'PUT' : 'POST',
      body: JSON.stringify(body),
    })
    emit('notify', {
      message: editing
        ? `手动任务「${body.name}」已更新`
        : `手动任务「${body.name}」已创建，覆盖 ${body.joinMappingIds.length} 间教室`,
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

function startEdit(task: ManualTask) {
  formOpen.value = true
  form.mode = 'edit'
  form.editingId = task.id
  form.roomIds = task.targets
    .map((target) => target.roomId)
    .filter((roomId): roomId is string => Boolean(roomId))
  form.joinName = task.joinName
  form.name = task.name
  form.confirmBeforeRun = task.confirmBeforeRun
  void nextTick(() => document.getElementById('manual-form')?.focus())
}

function requestDelete(task: ManualTask) {
  pendingDelete.value = task
}

async function confirmDelete() {
  const task = pendingDelete.value
  if (!task) return
  deleteBusy.value = true
  try {
    await request(`/api/manual-tasks/${task.id}`, { method: 'DELETE' })
    if (form.editingId === task.id) closeForm()
    pendingDelete.value = null
    historyCache[task.id] = []
    openTaskId.value = ''
    emit('notify', { message: `手动任务「${task.name}」已删除`, isError: false })
    emit('changed')
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    deleteBusy.value = false
  }
}

async function loadHistory(task: ManualTask, force = false) {
  if (!force && historyCache[task.id]) return
  historyLoadingId.value = task.id
  historyError.value = ''
  try {
    historyCache[task.id] = await readJson<ManualTaskExecution[]>(
      `/api/manual-tasks/${task.id}/executions`,
    )
  } catch (error) {
    historyError.value = toErrorMessage(error)
  } finally {
    historyLoadingId.value = ''
  }
}

async function toggleHistory(task: ManualTask) {
  if (openTaskId.value === task.id) {
    openTaskId.value = ''
    return
  }
  openTaskId.value = task.id
  await loadHistory(task)
}

function executionTone(status: string) {
  if (status === 'Succeeded') return 'success' as const
  if (status === 'Partial') return 'warning' as const
  if (status === 'Failed') return 'danger' as const
  return 'info' as const
}

function executionLabel(status: string) {
  if (status === 'Succeeded') return '全部成功'
  if (status === 'Partial') return '部分失败'
  if (status === 'Failed') return '全部失败'
  return status
}
</script>

<template>
  <div class="workspace-inner">
    <div class="automation-layout">
      <div class="stack">
        <header class="panel-toolbar">
          <div class="auto-toolbar-left">
            <div class="search-field">
              <UiIcon name="search" :size="15" />
              <input name="manualTaskQuery" v-model="taskQuery" class="input" type="search"
                placeholder="搜索任务/教室" aria-label="搜索手动任务" />
            </div>
            <select name="manualRoomFilter" v-model="roomFilter" class="select auto-room-filter"
              aria-label="按教室筛选">
              <option value="">全部教室</option>
              <option v-for="room in roomOptions" :key="room" :value="room">{{ room }}</option>
            </select>
          </div>
          <div class="auto-toolbar-right">
            <span class="text-xs muted">{{ filteredTasks.length }} / {{ manualTasks.length }}</span>
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
              <th>控制点</th>
              <th>执行确认</th>
              <th>操作</th>
            </tr>
          </thead>
        </table>
        <div v-if="filteredTasks.length" class="auto-list">
          <article v-for="task in filteredTasks" :id="`manual-task-${task.id}`" :key="task.id" class="auto-task">
            <div class="auto-task-main">
              <div class="auto-task-title">
                <strong>{{ task.name }}</strong>
                <span>手动按钮 · {{ task.joinName }}</span>
              </div>
              <div class="auto-task-cell">
                {{ taskRoomsLabel(task) }}
                <span>{{ task.targets.length }} 间教室</span>
              </div>
              <div class="auto-task-cell">
                {{ task.joinName }}
              </div>
              <div>
                <StatusPill :tone="task.confirmBeforeRun ? 'info' : 'neutral'"
                  :label="task.confirmBeforeRun ? '需确认' : '直接执行'" />
              </div>
              <div class="auto-task-actions">
                <button type="button" class="btn btn-quiet btn-sm" title="查看执行记录"
                  @click="toggleHistory(task)">
                  {{ openTaskId === task.id ? '收起' : '记录' }}
                </button>
                <button v-if="canOperate" type="button" class="btn btn-quiet btn-sm" title="修改教室范围与确认设置"
                  @click="startEdit(task)">
                  编辑
                </button>
                <button v-if="canOperate" type="button" class="btn btn-danger-quiet btn-sm"
                  title="删除任务及其执行记录" @click="requestDelete(task)">
                  删除
                </button>
              </div>
            </div>

            <section v-if="openTaskId === task.id" class="execution-panel">
              <div class="execution-head">
                <span>执行记录</span>
                <button type="button" class="btn btn-ghost btn-sm" :disabled="historyLoadingId === task.id"
                  @click="loadHistory(task, true)">
                  <UiIcon name="refresh" :size="13" />刷新
                </button>
              </div>
              <div v-if="historyLoadingId === task.id" class="loading-inline">
                <span class="spinner"></span>正在读取执行记录…
              </div>
              <p v-else-if="historyError" class="execution-error" role="alert">{{ historyError }}</p>
              <template v-else-if="historyCache[task.id]?.length">
                <div v-for="execution in historyCache[task.id]" :key="execution.id" class="execution-row">
                  <StatusPill :tone="executionTone(execution.status)" :label="executionLabel(execution.status)" />
                  <span class="tabular">{{ formatDateTime(execution.startedAtUtc) }}</span>
                  <span>
                    <span v-if="execution.error" class="execution-error">{{ execution.error }}</span>
                    <template v-else>
                      成功 {{ execution.succeededCount }} 间 · 失败 {{ execution.failedCount }} 间
                    </template>
                  </span>
                  <span class="text-xs muted tabular">
                    {{ execution.completedAtUtc ? formatDateTime(execution.completedAtUtc) : '—' }}
                  </span>
                </div>
              </template>
              <p v-else class="execution-empty">暂无执行记录。点击系统总览上的按钮后，这里会显示结果。</p>
            </section>
          </article>
        </div>

        <EmptyState v-else icon="bolt" title="没有符合条件的手动任务"
          description="调整筛选条件，或新建任务把多间教室的同名控制点聚合为一个总览按钮。" />
      </div>

      <Teleport to="body">
        <div v-if="formOpen" class="dialog-overlay auto-form-overlay" @click.self="closeForm"
          @keydown.esc.stop.prevent="closeForm">
          <aside id="manual-form" class="panel auto-form" role="dialog" aria-modal="true"
            aria-labelledby="manual-form-title" tabindex="-1">
            <header class="panel-head">
              <div class="panel-head-copy">
                <h2 id="manual-form-title">{{ form.mode === 'edit' ? '修改手动任务' : '新建手动任务' }}</h2>
                <p class="panel-sub">任务创建后会在系统总览显示为一个执行按钮。</p>
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
                  <input name="manualTaskName" v-model="form.name" class="input" maxlength="120"
                    placeholder="例如：全场上课" required />
                </label>

                <label class="checkbox">
                  <input name="confirmBeforeRun" v-model="form.confirmBeforeRun" type="checkbox" />
                  执行前要求二次确认
                </label>

                <p class="form-note">
                  覆盖 {{ selectedTargets.length }} 间教室；点击总览按钮时同时下发。
                  中控离线会记为失败，失败原因保存在执行记录中。
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

            <EmptyState v-else icon="connection" title="还没有可配置的控制点" description="先在配置中为教室接入中控并添加控制点。" />
          </aside>
        </div>
      </Teleport>
    </div>

    <ConfirmDialog :open="pendingDelete !== null" title="删除手动任务" :message="pendingDelete
      ? `删除「${pendingDelete.name}」将移除 ${pendingDelete.targets.length} 间教室的目标与全部执行记录，此操作不可撤销。`
      : ''" confirm-label="确认删除" tone="danger" @cancel="pendingDelete = null" @confirm="confirmDelete" />
    <span v-if="deleteBusy" class="sr-only" role="status">正在删除手动任务</span>
  </div>
</template>
