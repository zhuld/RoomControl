<script setup lang="ts">
import { computed, ref } from 'vue'
import { request, toErrorMessage } from '../api'
import { formatDateTime } from '../lib/format'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import UiIcon from './ui/UiIcon.vue'

// 配置文件只包含业务配置；导入会整体替换现有配置，因此统一走二次确认。
interface ConfigPreview {
  version?: number
  exportedAtUtc?: string
  buildings?: unknown
  rooms?: unknown
  schedules?: unknown
  manualTasks?: unknown
}

interface ImportSummary {
  buildingCount: number
  roomCount: number
  controllerCount: number
  joinCount: number
  scheduleCount: number
  manualTaskCount: number
}

const emit = defineEmits<{
  imported: []
  notify: [payload: { message: string; isError: boolean }]
}>()

const exporting = ref(false)
const importing = ref(false)
const error = ref('')
const pendingFile = ref<{ name: string; text: string; preview: ConfigPreview } | null>(null)

// 导入前按文件内容展示规模，帮助管理员确认选错了文件。
const previewCounts = computed(() => {
  const preview = pendingFile.value?.preview
  if (!preview) return []
  const rooms = Array.isArray(preview.rooms) ? (preview.rooms as Array<{ controller?: { joins?: unknown[] } | null }>) : []
  const controllers = rooms.filter((room) => Boolean(room?.controller))
  const joins = controllers.reduce((sum, room) => sum + (room.controller?.joins?.length ?? 0), 0)
  return [
    { label: '楼栋', value: Array.isArray(preview.buildings) ? preview.buildings.length : 0 },
    { label: '教室', value: rooms.length },
    { label: '中控', value: controllers.length },
    { label: '控制点', value: joins },
    { label: '自动任务', value: Array.isArray(preview.schedules) ? preview.schedules.length : 0 },
    { label: '手动任务', value: Array.isArray(preview.manualTasks) ? preview.manualTasks.length : 0 },
  ]
})

const exportedAtLabel = computed(() => {
  const value = pendingFile.value?.preview.exportedAtUtc
  return value ? formatDateTime(value) : ''
})

// 大配置文件的生成与导入可能超过默认 20 秒，单独放宽等待时间。
const TransferTimeoutMs = 60_000

function exportFileName() {
  const now = new Date()
  const pad = (value: number) => String(value).padStart(2, '0')
  return `roomcontrol-config-${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}.json`
}

async function exportConfig() {
  if (exporting.value) return
  exporting.value = true
  error.value = ''
  try {
    const response = await request('/api/config/export', { signal: AbortSignal.timeout(TransferTimeoutMs) })
    const blob = await response.blob()
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = exportFileName()
    document.body.append(link)
    link.click()
    link.remove()
    URL.revokeObjectURL(url)
    emit('notify', { message: '配置已导出，请妥善保管备份文件。', isError: false })
  } catch (err) {
    error.value = toErrorMessage(err)
  } finally {
    exporting.value = false
  }
}

async function onFileSelected(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  // 重置选择器，允许再次选择同一个文件。
  input.value = ''
  if (!file) return

  error.value = ''
  pendingFile.value = null
  let text = ''
  let preview: ConfigPreview
  try {
    text = await file.text()
    const parsed: unknown = JSON.parse(text)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      throw new Error('文件内容不是有效的配置对象。')
    }
    preview = parsed as ConfigPreview
  } catch (err) {
    error.value = err instanceof SyntaxError ? '所选文件不是有效的 JSON。' : toErrorMessage(err)
    return
  }

  if (preview.version !== 1) {
    error.value = `不支持的配置文件版本（${preview.version ?? '未知'}），请使用当前版本导出的文件。`
    return
  }

  pendingFile.value = { name: file.name, text, preview }
}

function cancelImport() {
  pendingFile.value = null
  error.value = ''
}

async function confirmImport() {
  const pending = pendingFile.value
  if (!pending || importing.value) return
  importing.value = true
  error.value = ''
  try {
    const response = await request('/api/config/import', {
      method: 'POST',
      body: pending.text,
      signal: AbortSignal.timeout(TransferTimeoutMs),
    })
    const summary = (await response.json()) as ImportSummary
    pendingFile.value = null
    emit('imported')
    emit('notify', {
      message: `已导入 ${summary.buildingCount} 个楼栋、${summary.roomCount} 间教室、${summary.controllerCount} 个中控与 ${summary.joinCount} 个控制点。`,
      isError: false,
    })
  } catch (err) {
    error.value = toErrorMessage(err)
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <div class="panel-body stack">
    <div class="banner is-info" role="note">
      <UiIcon name="transfer" :size="16" />
      <div>
        <strong>导入导出范围</strong>
        <span>包含站点信息、楼栋楼层、教室、中控、控制点、自动任务与手动任务；不包含账号、操作日志与任务执行记录。</span>
      </div>
    </div>

    <div class="stack">
      <div class="settings-section-head is-flush">
        <div>
          <h3>导出配置</h3>
          <p class="panel-sub">生成 JSON 备份文件，可用于恢复或迁移到其他实例。</p>
        </div>
      </div>
      <div class="form-actions is-start">
        <button type="button" class="btn btn-primary" :disabled="exporting" @click="exportConfig">
          {{ exporting ? '正在导出…' : '导出配置文件' }}
        </button>
      </div>
    </div>

    <hr class="divider" />

    <div class="stack">
      <div class="settings-section-head is-flush">
        <div>
          <h3>导入配置</h3>
          <p class="panel-sub">
            导入会清空并替换当前全部配置，账号与操作日志不受影响；请先导出备份。
          </p>
        </div>
      </div>
      <div class="form-actions is-start">
        <label class="btn btn-outline">
          选择配置文件
          <input type="file" name="configFile" accept="application/json,.json" hidden :disabled="importing || exporting"
            @change="onFileSelected" />
        </label>
      </div>
      <div v-if="pendingFile" class="config-summary">
        <strong>{{ pendingFile.name }}</strong>
        <span v-if="exportedAtLabel" class="form-note">导出于 {{ exportedAtLabel }}</span>
        <ul>
          <li v-for="item in previewCounts" :key="item.label">{{ item.label }}：{{ item.value }}</li>
        </ul>
      </div>
      <p v-if="error && !pendingFile" class="form-note is-error">{{ error }}</p>
    </div>

    <ConfirmDialog :open="pendingFile !== null" title="导入配置"
      :message="`导入「${pendingFile?.name ?? ''}」将清空并替换当前全部配置，且无法撤销。账号与操作日志不会被修改。`"
      confirm-label="清空并导入" tone="danger" @confirm="confirmImport" @cancel="cancelImport">
      <p v-if="error" class="form-note is-error">{{ error }}</p>
    </ConfirmDialog>
  </div>
</template>
