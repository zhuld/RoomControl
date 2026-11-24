<script setup lang="ts">
import { computed } from 'vue'
import type { CurrentUser, Workspace } from '../types'
import { userRoles, workspaces } from '../types'
import UiIcon from './ui/UiIcon.vue'

// 任务导航按「值守 → 操作 → 自动化 → 管理」排列，不暴露实现页面。
const props = defineProps<{
  currentUser: CurrentUser | null
  workspace: Workspace
  roomCount: number
  onlineCount: number
  attentionCount: number
  hubState: 'connecting' | 'connected' | 'disconnected'
  railCollapsed: boolean
}>()

const emit = defineEmits<{
  navigate: [workspace: Workspace]
  logout: []
  toggleCollapse: []
}>()

const canOperate = computed(() => props.currentUser?.role !== 'viewer')

// 侧栏按 workspaces 的顺序渲染；操作页对观看员隐藏。
const railItems = computed(() =>
  workspaces.filter((item) => item.value !== 'operate' || canOperate.value),
)

const roleLabel = computed(
  () => userRoles.find((role) => role.value === props.currentUser?.role)?.label ?? '访客',
)

const initials = computed(() => props.currentUser?.username.charAt(0).toUpperCase() || '访')

// 鼠标点击后释放焦点，让自动收起的导航在指针离开后立即收起；
// 键盘激活（detail === 0）保留焦点，以便继续用键盘浏览。
function openWorkspace(target: Workspace, event: MouseEvent) {
  if (event.detail > 0) (event.currentTarget as HTMLElement | null)?.blur()
  emit('navigate', target)
}

function toggleCollapse(event: MouseEvent) {
  if (event.detail > 0) (event.currentTarget as HTMLElement | null)?.blur()
  emit('toggleCollapse')
}

</script>

<template>
  <aside class="task-rail" aria-label="任务导航">
    <div class="rail-head">
      <button type="button" class="rail-collapse" :aria-expanded="!railCollapsed"
        :aria-label="railCollapsed ? '展开任务导航' : '收起任务导航'"
        :title="railCollapsed ? '展开导航' : '收起导航'" @click="toggleCollapse">
        <UiIcon :name="railCollapsed ? 'chevrons-right' : 'chevrons-left'" :size="20" />
      </button>
    </div>
    <div class="rail-scroll">
      <section v-for="item in railItems" :key="item.value" class="rail-section">
        <button type="button" class="rail-link" :class="{ active: workspace === item.value }" :title="item.label"
          :aria-label="item.value === 'overview' && canOperate && attentionCount
            ? `${item.label}，${attentionCount} 项待处理`
            : item.label" @click="openWorkspace(item.value, $event)">
          <UiIcon class="rail-icon" :name="item.icon" />
          <span class="rail-label">{{ item.label }}</span>
          <span v-if="item.value === 'overview' && canOperate" class="rail-badge"
            :class="{ 'is-alert': attentionCount > 0 }">{{ attentionCount }}</span>
        </button>
        <template v-if="item.value === 'overview'">
          <p v-if="!roomCount" class="rail-note">尚未接入教室，先完成配置。</p>
          <p v-else-if="canOperate && attentionCount" class="rail-note">{{ attentionCount }} 项需要处理，进入总览查看。</p>
          <p v-else class="rail-note">{{ roomCount }} 间教室 · {{ onlineCount }} 间在线。</p>
        </template>
      </section>
    </div>

    <div class="rail-footer">
      <div class="rail-user" :title="currentUser?.username">
        <span class="rail-avatar" aria-hidden="true">{{ initials }}</span>
        <span class="rail-user-copy">
          <strong>{{ currentUser?.username ?? '未登录' }}</strong>
          <span>{{ roleLabel }}</span>
        </span>
        <button type="button" class="rail-logout" title="退出登录" aria-label="退出登录" @click="emit('logout')">
          <UiIcon name="logout" :size="16" />
        </button>
      </div>

    </div>
  </aside>
</template>
