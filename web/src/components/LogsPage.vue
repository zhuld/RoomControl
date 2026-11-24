<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { readJson, toErrorMessage } from '../api'
import { formatClock, formatDateTime, formatRelative } from '../lib/format'
import { compactModeKey, actionSlotKey } from '../lib/layout'
import type { AuditLog } from '../types'
import EmptyState from './ui/EmptyState.vue'
import UiIcon from './ui/UiIcon.vue'

const categories = [
  { value: '', label: '全部' },
  { value: 'authentication', label: '认证' },
  { value: 'configuration', label: '配置' },
  { value: 'connection', label: '连接' },
  { value: 'control', label: '控制' },
  { value: 'state', label: '状态' },
  { value: 'schedule', label: '定时任务' },
]

// 操作列显示可读动作；原始接口保留在悬停提示里，便于排查。
const actionLabels: Record<string, string> = {
  'POST /api/auth/login': '登录',
  'POST /api/auth/logout': '退出登录',
  'POST /api/auth/change-password': '修改密码',
  'POST /api/users': '创建账号',
  'PUT /api/users/{id}': '更新账号',
  'DELETE /api/users/{id}': '删除账号',
  'PUT /api/branding': '更新站点信息',
  'PUT /api/logs/settings': '更新日志保留设置',
  'POST /api/buildings': '创建楼栋',
  'PUT /api/buildings/{buildingId}': '重命名楼栋',
  'DELETE /api/buildings/{buildingId}': '删除楼栋',
  'POST /api/buildings/{buildingId}/floors': '创建楼层',
  'PUT /api/floors/{floorId}': '重命名楼层',
  'DELETE /api/floors/{floorId}': '删除楼层',
  'POST /api/rooms': '创建教室',
  'PUT /api/rooms/{roomId}': '更新教室',
  'DELETE /api/rooms/{roomId}': '删除教室',
  'POST /api/rooms/{roomId}/controller': '配置中控',
  'PUT /api/controllers/{controllerId}': '更新中控',
  'DELETE /api/controllers/{controllerId}': '移除中控',
  'POST /api/controllers/{controllerId}/joins': '创建控制点',
  'PUT /api/joins/{mappingId}': '更新控制点',
  'DELETE /api/joins/{mappingId}': '删除控制点',
  'POST /api/joins/{mappingId}/copy': '复制控制点到教室',
  'POST /api/controllers/{controllerId}/joins/copy-all': '复制全部控制点到教室',
  'POST /api/joins/{mappingId}/pulse': '发送数字控制指令',
  'POST /api/schedules': '创建定时任务',
  'DELETE /api/schedules/{scheduleId}': '归档定时任务',
}

function normalizeAction(action: string) {
  return action.replace(/\{(\w+):[^}]+\}/g, '{$1}')
}

function actionLabel(action: string) {
  // 新记录直接保存可读动作；旧记录仍是接口模板，按映射翻译。
  if (!/^(GET|POST|PUT|PATCH|DELETE) \/api\//.test(action)) return action
  const normalized = normalizeAction(action)
  return actionLabels[normalized] ?? normalized
}

// 页面停留期间定时静默拉取；页面隐藏或暂停时停止，切回时立即刷新。
const AUTO_REFRESH_MS = 5000
const AUTO_REFRESH_STORAGE_KEY = 'roomcontrol.audit.autorefresh'

const selectedCategory = ref('')
const compactMode = inject(compactModeKey, ref(false))
const actionSlot = inject(actionSlotKey, ref<HTMLElement | null>(null))
const searchText = ref('')
const dateFrom = ref('')
const dateTo = ref('')
// 每页固定行数，管理员可按需要切换；选择保存在当前浏览器。
const PAGE_SIZE_OPTIONS = [20, 50, 100, 200]
const PAGE_SIZE_STORAGE_KEY = 'roomcontrol.audit.pagesize'
const storedPageSize = Number(localStorage.getItem(PAGE_SIZE_STORAGE_KEY))
const pageSize = ref(PAGE_SIZE_OPTIONS.includes(storedPageSize) ? storedPageSize : 100)
const currentPage = ref(1)
const logs = ref<AuditLog[]>([])
const loading = ref(false)
const errorMessage = ref('')
const lastRefreshedAt = ref<Date | null>(null)
const autoRefresh = ref(localStorage.getItem(AUTO_REFRESH_STORAGE_KEY) !== '0')
let refreshTimer: ReturnType<typeof setInterval> | undefined
let requestInFlight = false
let reloadPending = false
let pointerDownInTable = false

const dateRangeInvalid = computed(
  () => Boolean(dateFrom.value && dateTo.value && dateFrom.value > dateTo.value),
)

watch(autoRefresh, (enabled) => {
  localStorage.setItem(AUTO_REFRESH_STORAGE_KEY, enabled ? '1' : '0')
})

watch(pageSize, (size) => {
  localStorage.setItem(PAGE_SIZE_STORAGE_KEY, String(size))
  currentPage.value = 1
})

// 分类、关键词与日期变化后回到第一页，避免停留在超出范围的页码。
watch([selectedCategory, searchText], () => {
  currentPage.value = 1
})

// 日期区间由服务端筛选，变更后立即重新读取；分类与搜索仍在前端组合过滤。
watch([dateFrom, dateTo], () => {
  currentPage.value = 1
  void loadLogs()
})

const categoryCounts = computed(() => {
  const counts = new Map<string, number>()
  for (const log of logs.value) {
    counts.set(log.category, (counts.get(log.category) ?? 0) + 1)
  }
  return counts
})

const filteredLogs = computed(() => {
  const search = searchText.value.trim().toLocaleLowerCase()
  return logs.value.filter((log) => {
    if (selectedCategory.value && log.category !== selectedCategory.value) return false
    if (!search) return true
    return `${log.action} ${actionLabel(log.action)} ${log.target} ${log.actor}`
      .toLocaleLowerCase()
      .includes(search)
  })
})

const failureCount = computed(
  () => filteredLogs.value.filter((log) => log.statusCode >= 400).length,
)

const totalPages = computed(
  () => Math.max(1, Math.ceil(filteredLogs.value.length / pageSize.value)),
)

const pagedLogs = computed(() => {
  const start = (currentPage.value - 1) * pageSize.value
  return filteredLogs.value.slice(start, start + pageSize.value)
})

const rangeStart = computed(() =>
  filteredLogs.value.length ? (currentPage.value - 1) * pageSize.value + 1 : 0,
)

const rangeEnd = computed(() =>
  Math.min(currentPage.value * pageSize.value, filteredLogs.value.length),
)

watch(totalPages, (pages) => {
  if (currentPage.value > pages) currentPage.value = pages
})

function goToPage(page: number) {
  currentPage.value = Math.min(Math.max(page, 1), totalPages.value)
}

function toDateBoundary(date: string, endOfDay: boolean) {
  // 日期输入是浏览器本地日期，转换为 UTC 时间点交给服务端筛选。
  return new Date(`${date}T${endOfDay ? '23:59:59.999' : '00:00:00'}`).toISOString()
}

function buildLogsUrl() {
  const params = new URLSearchParams()
  if (dateFrom.value) params.set('from', toDateBoundary(dateFrom.value, false))
  if (dateTo.value) params.set('to', toDateBoundary(dateTo.value, true))
  const query = params.toString()
  return query ? `/api/logs?${query}` : '/api/logs'
}

function clearDateRange() {
  dateFrom.value = ''
  dateTo.value = ''
}

async function loadLogs(options: { silent?: boolean } = {}) {
  if (dateRangeInvalid.value) {
    errorMessage.value = '开始日期不能晚于结束日期，请调整筛选区间。'
    return
  }
  // 手动刷新与自动刷新不并发；期间产生的刷新请求在本次完成后补读一次。
  if (requestInFlight) {
    reloadPending = true
    return
  }
  requestInFlight = true
  const silent = options.silent === true
  if (!silent) {
    loading.value = true
    errorMessage.value = ''
  }
  try {
    logs.value = await readJson<AuditLog[]>(buildLogsUrl())
    errorMessage.value = ''
    lastRefreshedAt.value = new Date()
  } catch (error) {
    if (!silent || !logs.value.length) {
      errorMessage.value = toErrorMessage(error)
    }
  } finally {
    requestInFlight = false
    if (!silent) loading.value = false
    if (reloadPending) {
      reloadPending = false
      void loadLogs()
    }
  }
}

function refreshInBackground() {
  // 正在表格内选择文本时暂停自动刷新，避免重渲染清除已选内容。
  if (!autoRefresh.value || document.hidden || pointerDownInTable || hasLogTableSelection()) return
  void loadLogs({ silent: true })
}

function hasLogTableSelection() {
  const selection = window.getSelection()
  if (!selection || selection.isCollapsed || !selection.toString()) return false
  const table = document.querySelector('.audit-table')
  if (!table) return false
  // 选中范围可能从表格内拖到表格外，anchor 或 focus 任一在表内即视为正在选择。
  return [selection.anchorNode, selection.focusNode].some(
    (node) => node !== null && table.contains(node),
  )
}

function handleTablePointerDown() {
  pointerDownInTable = true
}

function handlePointerUp() {
  pointerDownInTable = false
}

function handleVisibilityChange() {
  if (!document.hidden) refreshInBackground()
}

function categoryLabel(category: string) {
  return categories.find((item) => item.value === category)?.label ?? category
}

function countForCategory(category: string) {
  if (!category) return logs.value.length
  return categoryCounts.value.get(category) ?? 0
}

function statusLabel(log: AuditLog) {
  if (log.statusCode <= 0) return '—'
  return log.statusCode >= 400 ? `失败 ${log.statusCode}` : `完成 ${log.statusCode}`
}

onMounted(() => {
  void loadLogs()
  refreshTimer = setInterval(refreshInBackground, AUTO_REFRESH_MS)
  document.addEventListener('visibilitychange', handleVisibilityChange)
  document.addEventListener('pointerup', handlePointerUp)
  document.addEventListener('pointercancel', handlePointerUp)
})

onBeforeUnmount(() => {
  if (refreshTimer !== undefined) clearInterval(refreshTimer)
  document.removeEventListener('visibilitychange', handleVisibilityChange)
  document.removeEventListener('pointerup', handlePointerUp)
  document.removeEventListener('pointercancel', handlePointerUp)
})
</script>

<template>
  <div class="workspace-inner">
    <header class="workspace-header">
      <div class="workspace-header-copy">
        <p class="eyebrow">管理 · 审计</p>
        <h1>操作记录</h1>
        <p>追踪认证、配置变更、控制指令、中控连接、状态变化与定时任务。可按日期区间筛选；超过保留天数的记录会自动清理，保留天数在「配置与接入 · 站点与外观」中调整。</p>
      </div>
      <Teleport v-if="actionSlot" :to="actionSlot" :disabled="!compactMode">
        <div class="workspace-actions">
          <!-- <span class="audit-refresh">
            <span class="dot" :class="autoRefresh ? 'is-online' : 'is-unknown'"></span>
            {{ autoRefresh ? '自动刷新 · 每 5 秒' : '自动刷新已暂停' }}
            <span class="muted">最近 {{ lastRefreshedAt ? formatClock(lastRefreshedAt) : '—' }}</span>
          </span> -->
          <button type="button" class="btn btn-quiet btn-sm" :aria-pressed="autoRefresh"
            @click="autoRefresh = !autoRefresh">
            <UiIcon :name="autoRefresh ? 'pause' : 'play'" :size="14" />
            {{ autoRefresh ? '暂停' : '继续' }}
          </button>
          <button type="button" class="btn btn-quiet" :disabled="loading" @click="loadLogs()">
            <UiIcon name="refresh" :size="16" />{{ loading ? '正在读取…' : '刷新' }}
          </button>
        </div>
      </Teleport>
    </header>

    <section class="panel" aria-label="操作记录">
      <header class="panel-toolbar audit-toolbar">
        <div class="audit-toolbar-left">
          <div class="chip-row" role="group" aria-label="按分类筛选">
            <button v-for="category in categories" :key="category.value" type="button" class="chip"
              :class="{ active: selectedCategory === category.value }"
              :aria-pressed="selectedCategory === category.value" @click="selectedCategory = category.value">
              {{ category.label }}
              <span class="chip-count">{{ countForCategory(category.value) }}</span>
            </button>
          </div>
        </div>
        <div class="audit-toolbar-right">
          <div class="audit-date-range" role="group" aria-label="按日期区间筛选">
            <UiIcon name="calendar" :size="15" />
            <input v-model="dateFrom" name="dateFrom" class="input" type="date" aria-label="开始日期" />
            <span class="muted">至</span>
            <input v-model="dateTo" name="dateTo" class="input" type="date" aria-label="结束日期" />
            <button v-if="dateFrom || dateTo" type="button" class="btn btn-quiet btn-sm" @click="clearDateRange">
              清除
            </button>
          </div>
          <div class="search-field">
            <UiIcon name="search" :size="15" />
            <input name="searchText" v-model="searchText" class="input" type="search" placeholder="搜索对象、操作者或操作"
              aria-label="搜索对象、操作者或操作" />
          </div>
        </div>
      </header>

      <div v-if="errorMessage" class="banner is-danger audit-banner" role="alert">
        <UiIcon name="alert" :size="16" />
        <div>
          <strong>读取记录失败</strong>
          <p>{{ errorMessage }}</p>
          <p v-if="logs.length">下方仍显示上一次成功读取的记录。</p>
        </div>
      </div>

      <div v-if="loading && !logs.length" class="audit-message" role="status">
        <span class="loading-inline"><span class="spinner"></span>正在加载操作记录…</span>
      </div>
      <EmptyState v-else-if="!filteredLogs.length" icon="audit"
        :title="searchText || selectedCategory || dateFrom || dateTo ? '没有符合条件的记录' : '暂无操作记录'"
        :description="searchText || selectedCategory || dateFrom || dateTo
          ? '调整分类、搜索词或日期区间后再试。'
          : '完成登录、配置或控制操作后会在此出现。'" />
      <div v-else class="table-wrap audit-table-wrap" @pointerdown="handleTablePointerDown">
        <table class="table audit-table">
          <thead>
            <tr>
              <th>时间</th>
              <th>分类</th>
              <th>对象</th>
              <th>操作</th>
              <th>操作者</th>
              <th>结果</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="log in pagedLogs" :key="log.id">
              <td class="log-time">
                <span class="is-strong">{{ formatDateTime(log.occurredAtUtc) }}</span>
                <span class="block text-2xs muted">{{ formatRelative(log.occurredAtUtc) }}</span>
              </td>
              <td>
                <span class="log-category" :class="`category-${log.category}`">{{ categoryLabel(log.category) }}</span>
              </td>
              <td><span class="log-target" :title="log.target">{{ log.target || '—' }}</span></td>
              <td class="log-action" :title="log.action">{{ actionLabel(log.action) }}</td>
              <td>{{ log.actor }}</td>
              <td>
                <span v-if="log.statusCode > 0" class="log-status" :class="{ 'is-failed': log.statusCode >= 400 }">
                  <span class="dot" :class="log.statusCode >= 400 ? 'is-danger' : 'is-success'"></span>
                  {{ statusLabel(log) }}
                </span>
                <span v-else class="log-status is-pending">—</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <footer class="panel-foot audit-foot">
        <span>
          显示 {{ rangeStart }}–{{ rangeEnd }} 条 / 共 {{ filteredLogs.length }} 条
          <template v-if="logs.length !== filteredLogs.length">（已读取 {{ logs.length }} 条）</template>
          <template v-if="failureCount"> · 其中 {{ failureCount }} 条失败</template>
        </span>
        <div class="audit-pagination">
          <label class="audit-page-size">
            每页
            <select v-model.number="pageSize" class="select" aria-label="每页行数">
              <option v-for="size in PAGE_SIZE_OPTIONS" :key="size" :value="size">{{ size }}</option>
            </select>
            条
          </label>
          <button type="button" class="btn btn-quiet btn-sm" :disabled="currentPage <= 1"
            @click="goToPage(currentPage - 1)">
            上一页
          </button>
          <span class="audit-page-indicator">{{ currentPage }} / {{ totalPages }}</span>
          <button type="button" class="btn btn-quiet btn-sm" :disabled="currentPage >= totalPages"
            @click="goToPage(currentPage + 1)">
            下一页
          </button>
        </div>
        <span>
          {{ autoRefresh ? '每 5 秒自动刷新' : '自动刷新已暂停' }} · 最近刷新
          {{ lastRefreshedAt ? formatClock(lastRefreshedAt) : '—' }}
        </span>
      </footer>
    </section>
  </div>
</template>
