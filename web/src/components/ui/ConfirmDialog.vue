<script setup lang="ts">
import {
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogOverlay,
  AlertDialogPortal,
  AlertDialogRoot,
  AlertDialogTitle,
} from 'reka-ui'
import { ref, watch } from 'vue'
import UiIcon from './UiIcon.vue'

// 所有需要二次确认的操作共用同一对话框，避免各页面出现不同的确认行为。
const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    message?: string
    confirmLabel?: string
    cancelLabel?: string
    tone?: 'default' | 'danger'
    icon?: string
  }>(),
  {
    message: '',
    confirmLabel: '确认',
    cancelLabel: '取消',
    tone: 'default',
    icon: 'alert',
  },
)

const emit = defineEmits<{
  confirm: []
  cancel: []
}>()

// 确认按钮由调用方关闭对话框；记录来源，避免关闭事件再次发出 cancel。
const confirmed = ref(false)
const cancelled = ref(false)
let lastFocused: HTMLElement | null = null

watch(
  () => props.open,
  (open) => {
    if (!open) return
    confirmed.value = false
    cancelled.value = false
    lastFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
  },
  { immediate: true },
)

function handleOpenChange(open: boolean) {
  if (open) return
  if (confirmed.value) {
    confirmed.value = false
    return
  }
  handleCancel()
}

function handleCancel() {
  if (cancelled.value) return
  cancelled.value = true
  emit('cancel')
}

function handleConfirm() {
  confirmed.value = true
  emit('confirm')
}

function restoreFocus(event: Event) {
  event.preventDefault()
  lastFocused?.focus()
}
</script>

<template>
  <AlertDialogRoot :open="open" @update:open="handleOpenChange">
    <AlertDialogPortal>
      <AlertDialogOverlay class="dialog-overlay" @click="handleCancel" />
      <AlertDialogContent class="dialog reka-dialog" aria-modal="true"
        :class="{ 'is-danger': tone === 'danger' }" @close-auto-focus="restoreFocus"
        @keydown.esc.stop.prevent="handleCancel">
        <div class="dialog-head">
          <span class="dialog-icon"><UiIcon :name="icon" :size="34" /></span>
          <div class="dialog-head-copy">
            <AlertDialogTitle>{{ title }}</AlertDialogTitle>
            <AlertDialogDescription as="span" :class="{ 'sr-only': !message }">
              {{ message || '请确认是否继续操作。' }}
            </AlertDialogDescription>
          </div>
        </div>
        <div v-if="$slots.default" class="dialog-body">
          <slot />
        </div>
        <div class="dialog-actions">
          <AlertDialogCancel as-child>
            <button type="button" class="btn btn-quiet">{{ cancelLabel }}</button>
          </AlertDialogCancel>
          <button type="button" class="btn" :class="tone === 'danger' ? 'btn-danger-quiet' : 'btn-primary'"
            @click="handleConfirm">
            {{ confirmLabel }}
          </button>
        </div>
      </AlertDialogContent>
    </AlertDialogPortal>
  </AlertDialogRoot>
</template>
