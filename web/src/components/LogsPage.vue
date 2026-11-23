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
  'POST /api/joins/{mappingId}/pulse': '发送脉冲',
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
const logs = ref<AuditLog[]>([])
const loading = ref(false)
const errorMessage = ref('')
const lastRefreshedAt = ref<Date | null>(null)
const autoRefresh = ref(localStorage.getItem(AUTO_REFRESH_STORAGE_KEY) !== '0')
let refreshTimer: ReturnType<typeof setInterval> | undefined
let requestInFlight = false

watch(autoRefresh, (enabled) => {
  localStorage.setItem(AUTO_REFRESH_STORAGE_KEY, enabled ? '1' : '0')
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
  () => logs.value.filter((log) => log.statusCode >= 400).length,
)

async function loadLogs(options: { silent?: boolean } = {}) {
  // 手动刷新与自动刷新不并发；静默刷新失败时保留已有记录，不覆盖列表。
  if (requestInFlight) return
  requestInFlight = true
  const silent = options.silent === true
  if (!silent) {
    loading.value = true
    errorMessage.value = ''
  }
  try {
    logs.value = await readJson<AuditLog[]>('/api/logs?limit=500')
    errorMessage.value = ''
    lastRefreshedAt.value = new Date()
  } catch (error) {
    if (!silent || !logs.value.length) {
      errorMessage.value = toErrorMessage(error)
    }
  } finally {
    requestInFlight = false
    if (!silent) loading.value = false
  }
}

function refreshInBackground() {
  if (!autoRefresh.value || document.hidden) return
  void loadLogs({ silent: true })
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
})

onBeforeUnmount(() => {
  if (refreshTimer !== undefined) clearInterval(refreshTimer)
  document.removeEventListener('visibilitychange', handleVisibilityChange)
})
</script>

<template>
  <div class="workspace-inner">
    <header class="workspace-header">
      <div class="workspace-header-copy">
        <p class="eyebrow">管理 · 审计</p>
        <h1>操作记录</h1>
        <p>追踪认证、配置变更、控制指令、中控连接、状态变化与定时任务。最多读取最近 500 条。</p>
      </div>
      <Teleport v-if="actionSlot" :to="actionSlot" :disabled="!compactMode">
        <div class="workspace-actions">
          <span class="audit-refresh">
            <span class="dot" :class="autoRefresh ? 'is-online' : 'is-unknown'"></span>
            {{ autoRefresh ? '自动刷新 · 每 5 秒' : '自动刷新已暂停' }}
            <span class="muted">最近 {{ lastRefreshedAt ? formatClock(lastRefreshedAt) : '—' }}</span>
          </span>
          <button
            type="button"
            class="btn btn-quiet btn-sm"
            :aria-pressed="autoRefresh"
            @click="autoRefresh = !autoRefresh"
          >
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
            <button
              v-for="category in categories"
              :key="category.value"
              type="button"
              class="chip"
              :class="{ active: selectedCategory === category.value }"
              :aria-pressed="selectedCategory === category.value"
              @click="selectedCategory = category.value"
            >
              {{ category.label }}
              <span class="chip-count">{{ countForCategory(category.value) }}</span>
            </button>
          </div>
        </div>
        <div class="audit-toolbar-right">
          <div class="search-field">
            <UiIcon name="search" :size="15" />
            <input
              name="searchText"
              v-model="searchText"
              class="input"
              type="search"
              placeholder="搜索对象、操作者或操作"
              aria-label="搜索对象、操作者或操作"
            />
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
      <EmptyState
        v-else-if="!filteredLogs.length"
        icon="audit"
        :title="searchText || selectedCategory ? '没有符合条件的记录' : '暂无操作记录'"
        :description="searchText || selectedCategory ? '调整分类或搜索词后再试。' : '完成登录、配置或控制操作后会在此出现。'"
      />
      <div v-else class="table-wrap audit-table-wrap">
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
            <tr v-for="log in filteredLogs" :key="log.id">
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
                <span
                  v-if="log.statusCode > 0"
                  class="log-status"
                  :class="{ 'is-failed': log.statusCode >= 400 }"
                >
                  <span class="dot" :class="log.statusCode >= 400 ? 'is-danger' : 'is-success'"></span>
                  {{ statusLabel(log) }}
                </span>
                <span v-else class="log-status is-pending">—</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <footer class="panel-foot">
        <span>
          显示 {{ filteredLogs.length }} 条 / 已读取 {{ logs.length }} 条
          <template v-if="failureCount"> · 其中 {{ failureCount }} 条失败</template>
        </span>
        <span>
          {{ autoRefresh ? '每 5 秒自动刷新' : '自动刷新已暂停' }} · 最近刷新
          {{ lastRefreshedAt ? formatClock(lastRefreshedAt) : '—' }}
        </span>
      </footer>
    </section>
  </div>
</template>
