<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { readJson, request, toErrorMessage } from '../api'
import { formatDateTime } from '../lib/format'
import { type CurrentUser, type User, type UserRole, userRoles } from '../types'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import EmptyState from './ui/EmptyState.vue'
import StatusPill from './ui/StatusPill.vue'
import UiIcon from './ui/UiIcon.vue'

// 用户列表与表单；删除操作统一走 ConfirmDialog。
const props = defineProps<{
  currentUser: CurrentUser
}>()

const emit = defineEmits<{
  notify: [payload: { message: string; isError: boolean }]
}>()

const busy = ref(false)
const loading = ref(false)
const loadError = ref('')
const users = ref<User[]>([])
const showForm = ref(false)
const editingId = ref<string | null>(null)
const pendingDelete = ref<User | null>(null)

const form = reactive({
  username: '',
  password: '',
  role: 'viewer' as UserRole,
  isActive: true,
})

const editForm = reactive({
  id: '',
  username: '',
  password: '',
  role: 'viewer' as UserRole,
  isActive: true,
})

const roleOptions = computed(() => userRoles)
const canManageUsers = computed(() => props.currentUser.role === 'admin')
const selectedRole = computed(() =>
  userRoles.find((role) => role.value === (editingId.value ? editForm.role : form.role)),
)

// 两组表单共用模板输入项，计算属性把读写路由到当前编辑模式。
const editingUsername = computed({
  get: () => (editingId.value ? editForm.username : form.username),
  set: (value: string) => {
    if (editingId.value) editForm.username = value
    else form.username = value
  },
})
const editingPassword = computed({
  get: () => (editingId.value ? editForm.password : form.password),
  set: (value: string) => {
    if (editingId.value) editForm.password = value
    else form.password = value
  },
})
const editingRole = computed({
  get: () => (editingId.value ? editForm.role : form.role),
  set: (value: UserRole) => {
    if (editingId.value) editForm.role = value
    else form.role = value
  },
})
const editingIsActive = computed({
  get: () => (editingId.value ? editForm.isActive : form.isActive),
  set: (value: boolean) => {
    if (editingId.value) editForm.isActive = value
    else form.isActive = value
  },
})

async function loadUsers() {
  loading.value = true
  loadError.value = ''
  try {
    users.value = await readJson<User[]>('/api/users')
  } catch (error) {
    loadError.value = toErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function openCreateForm() {
  showForm.value = true
  editingId.value = null
  form.username = ''
  form.password = ''
  form.role = 'viewer'
  form.isActive = true
}

function openEditForm(user: User) {
  showForm.value = true
  editingId.value = user.id
  editForm.id = user.id
  editForm.username = user.username
  editForm.password = ''
  editForm.role = user.role
  editForm.isActive = user.isActive
}

function closeForm() {
  showForm.value = false
  editingId.value = null
}

async function saveUser() {
  if (editingId.value) {
    await updateUser()
  } else {
    await createUser()
  }
}

async function createUser() {
  if (!form.username.trim() || !form.password) {
    emit('notify', { message: '用户名和密码均为必填项。', isError: true })
    return
  }

  busy.value = true
  try {
    await request('/api/users', {
      method: 'POST',
      body: JSON.stringify({
        username: form.username.trim(),
        password: form.password,
        role: form.role,
        isActive: form.isActive,
      }),
    })
    closeForm()
    await loadUsers()
    emit('notify', { message: '用户已创建。', isError: false })
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    busy.value = false
  }
}

async function updateUser() {
  if (!editingId.value) return

  busy.value = true
  try {
    await request(`/api/users/${editingId.value}`, {
      method: 'PUT',
      body: JSON.stringify({
        password: editForm.password || undefined,
        role: editForm.role,
        isActive: editForm.isActive,
      }),
    })
    closeForm()
    await loadUsers()
    emit('notify', { message: '用户已更新。', isError: false })
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    busy.value = false
  }
}

function requestDelete(user: User) {
  // 服务端同样禁止删除当前账号；前端提前提示以避免无效请求。
  if (user.username === props.currentUser.username) {
    emit('notify', { message: '不能删除当前登录的用户。', isError: true })
    return
  }
  pendingDelete.value = user
}

async function confirmDelete() {
  const user = pendingDelete.value
  if (!user) return
  pendingDelete.value = null

  busy.value = true
  try {
    await request(`/api/users/${user.id}`, { method: 'DELETE' })
    await loadUsers()
    emit('notify', { message: `用户“${user.username}”已删除。`, isError: false })
  } catch (error) {
    emit('notify', { message: toErrorMessage(error), isError: true })
  } finally {
    busy.value = false
  }
}

function roleClass(role: UserRole) {
  return `is-${role === 'admin' ? 'admin' : role === 'operator' ? 'operator' : 'viewer'}`
}

onMounted(() => {
  void loadUsers()
})
</script>

<template>
  <div class="workspace-inner">
    <div class="automation-layout">
      <div class="panel-toolbar">
        <div class="row row-wrap">
          <span class="text-sm muted">{{ users.length }} 个账号</span>
          <span v-if="loading" class="loading-inline"><span class="spinner"></span>正在读取…</span>
          <span v-else class="text-xs muted">角色变更在下次请求时立即生效</span>
        </div>
        <div class="row">
          <button type="button" class="btn btn-quiet btn-sm" :disabled="loading" @click="loadUsers">
            <UiIcon name="refresh" :size="14" />刷新
          </button>
          <button v-if="canManageUsers" type="button" class="btn btn-primary btn-sm" @click="openCreateForm">
            <UiIcon name="plus" :size="14" />添加用户
          </button>
        </div>
      </div>

      <div v-if="loadError" class="banner is-danger user-load-error" role="alert">
        <UiIcon name="alert" :size="16" />
        <div>
          <strong>用户列表读取失败</strong>
          <p>{{ loadError }}</p>
        </div>
        <button type="button" class="btn btn-quiet btn-sm" :disabled="loading" @click="loadUsers">重试</button>
      </div>
      <div v-if="users.length">
        <div class="main-table-head user-table">
          <div>用户名</div>
          <div>角色</div>
          <div>状态</div>
          <div>创建时间</div>
          <div class="table-actions">操作</div>
        </div>
        <div class="table-wrap room-table-wrap"">
        <div v-for="user in users" :key="user.id" class="main-table-body user-table">
          <div>
            <span class="is-strong">{{ user.username }}</span>
            <span v-if="user.username === currentUser.username" class="user-self">当前账号</span>
          </div>
          <div>
            <span class="role-badge" :class="roleClass(user.role)">{{ user.roleDisplayName }}</span>
          </div>
          <div>
            <StatusPill :tone="user.isActive ? 'success' : 'neutral'" :label="user.isActive ? '启用' : '禁用'" dot />
          </div>
          <span class="text-xs muted">{{ formatDateTime(user.createdAt) }}</span>

          <div class="table-actions">
            <button v-if="canManageUsers" class="btn btn-quiet btn-sm" @click="openEditForm(user)">
              编辑
            </button>
            <button v-if="canManageUsers && user.username !== currentUser.username" class="btn btn-danger-quiet btn-sm"
              @click="requestDelete(user)">
              删除
            </button>
          </div>
        </div>
      </div>
    </div>
    <EmptyState v-else-if="!loading && !loadError" icon="user" title="还没有账号" description="添加管理员、操作员或观看员，并分配最小必要权限。">
      <button v-if="canManageUsers" type="button" class="btn btn-primary btn-sm empty-action" @click="openCreateForm">
        添加用户
      </button>
    </EmptyState>

    <div v-if="showForm" class="dialog-overlay" @click.self="closeForm" @keydown.esc.stop.prevent="closeForm">
      <section class="dialog" role="dialog" aria-modal="true" aria-labelledby="user-form-title">
        <h2 id="user-form-title">{{ editingId ? '编辑用户' : '添加用户' }}</h2>
        <form class="user-form" @submit.prevent="saveUser">
          <div class="dialog-body form-grid">
            <label class="field">
              用户名
              <input name="username" autocomplete="off" v-model="editingUsername" class="input"
                placeholder="例如：operator01" maxlength="60" :disabled="Boolean(editingId)" required />
            </label>
            <label class="field">
              密码
              <input name="password" v-model="editingPassword" class="input" type="password"
                :placeholder="editingId ? '留空表示不修改密码' : '设置初始密码'" minlength="6" maxlength="128" />
            </label>
            <label class="field">
              角色
              <select name="role" v-model="editingRole" class="select">
                <option v-for="role in roleOptions" :key="role.value" :value="role.value">
                  {{ role.label }}
                </option>
              </select>
            </label>
            <label class="checkbox">
              <input name="isActive" v-model="editingIsActive" type="checkbox" />
              账号启用
            </label>
          </div>
          <p v-if="selectedRole" class="form-note">{{ selectedRole.description }}</p>
          <p v-if="editingId" class="form-note">用户名不可修改；密码留空表示保持原密码。</p>
          <p v-else class="form-note">初始密码可在首次登录后由用户自行修改。</p>
          <div class="form-actions">
            <button type="button" class="btn btn-quiet" @click="closeForm">取消</button>
            <button type="submit" class="btn btn-primary" :disabled="busy">
              {{ busy ? '保存中…' : '保存' }}
            </button>
          </div>
        </form>
      </section>
    </div>

    <ConfirmDialog :open="pendingDelete !== null" title="确认删除用户"
      :message="pendingDelete ? `确定要删除用户“${pendingDelete.username}”吗？此操作无法撤销。` : ''" confirm-label="确认删除" tone="danger"
      @cancel="pendingDelete = null" @confirm="confirmDelete" />
  </div>
  </div>
</template>
