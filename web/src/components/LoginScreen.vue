<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, readJson, request, toErrorMessage } from '../api'
import { userRoles, type Branding, type CurrentUser, type LoginAccount } from '../types'
import BrandLogo from './BrandLogo.vue'
import UiIcon from './ui/UiIcon.vue'

// 登录页自行管理表单与请求，成功后把会话身份交给根组件继续加载站点数据。
defineProps<{ branding: Branding }>()

const emit = defineEmits<{
  authenticated: [user: CurrentUser]
}>()

const username = ref('')
const password = ref('')
const busy = ref(false)
const errorMessage = ref('')
const loginAccounts = ref<LoginAccount[]>([])

const selectedLoginRole = computed(() => {
  const account = loginAccounts.value.find((item) => item.username === username.value)
  return userRoles.find((role) => role.value === account?.role) ?? null
})

// 账号列表只用于提示可登录的账号与角色；接口不可用时回退为手工输入。
async function loadAccounts() {
  try {
    loginAccounts.value = await readJson<LoginAccount[]>('/api/auth/accounts')
  } catch {
    loginAccounts.value = []
  }
}

// 登录接口的 401 表示账号或密码无效，通用提示容易误导为会话过期。
function loginFailureMessage(error: unknown) {
  return error instanceof ApiError && error.status === 401
    ? new Error('用户名或密码错误，或账号已被锁定/禁用。')
    : error
}

async function login() {
  busy.value = true
  errorMessage.value = ''
  try {
    await request('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username: username.value, password: password.value }),
    })
  } catch (error) {
    errorMessage.value = toErrorMessage(loginFailureMessage(error))
    busy.value = false
    return
  }

  password.value = ''
  try {
    const me = await readJson<CurrentUser>('/api/auth/me')
    emit('authenticated', { username: me.username, role: me.role })
  } catch {
    // 登录接口成功但会话未生效时留在登录页，提示重新登录。
    errorMessage.value = '登录状态无效，请重试。'
    busy.value = false
  }
}

onMounted(() => {
  void loadAccounts()
})
</script>

<template>
  <main class="login-screen">
    <section class="login-shell">
      <div class="login-intro">
        <div class="row">
          <BrandLogo class="login-brand" :logo-data-url="branding.logoDataUrl" :label="branding.displayName" />
          <span class="brand-copy">
            <strong>{{ branding.displayName }}</strong>
            <span>{{ branding.subtitle || 'ROOM CONTROL PLATFORM' }}</span>
          </span>
        </div>

        <ul class="login-features">
          <li class="login-feature">
            <UiIcon name="connection" :size="16" />
            <div><strong>实时状态</strong>中控连接与 Join 回报通过实时通道同步。</div>
          </li>
          <li class="login-feature">
            <UiIcon name="shield" :size="16" />
            <div><strong>受控操作</strong>高风险指令可要求二次确认，角色权限即时生效。</div>
          </li>
          <li class="login-feature">
            <UiIcon name="audit" :size="16" />
            <div><strong>可追溯</strong>每次下发都有会话回执，服务端保留操作记录。</div>
          </li>
        </ul>
      </div>

      <div class="login-card">
        <div>
          <h2>登录平台</h2>
          <p class="panel-sub">使用管理员分配的账号进入。</p>
        </div>
        <form class="login-form" @submit.prevent="login">
          <label class="field">
            用户名
            <select name="username" v-if="loginAccounts.length" v-model="username" class="select" autocomplete="username" required>
              <option value="" disabled>请选择账号</option>
              <option v-for="account in loginAccounts" :key="account.username" :value="account.username">
                {{ account.username }}
              </option>
            </select>
            <input name="username" v-else v-model="username" class="input" maxlength="60" autocomplete="username" required />
          </label>
          <div v-if="selectedLoginRole" class="login-role">
            <strong>{{ selectedLoginRole.label }}</strong>
            <span>{{ selectedLoginRole.description }}</span>
          </div>
          <div v-else class="login-role">
            <strong>提示</strong>
            <span>请联系管理员确认账号权限</span>
          </div>
          <label class="field">
            密码
            <input name="password" placeholder="输入密码" v-model="password" class="input" type="password" maxlength="128" autocomplete="current-password" required />
          </label>
          <p v-if="errorMessage" class="login-error" role="alert">{{ errorMessage }}</p>
          <button class="btn btn-primary btn-block" :disabled="busy">
            {{ busy ? '正在登录…' : '登录平台' }}
          </button>
        </form>
        <p class="login-footnote">账号由管理员分配；连续失败会触发登录限制。</p>
      </div>
    </section>
  </main>
</template>
