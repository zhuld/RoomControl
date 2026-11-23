<script setup lang="ts">
import { nextTick, ref, useId, watch } from 'vue'
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

const titleId = useId()
const messageId = useId()
const cancelButton = ref<HTMLButtonElement | null>(null)

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    await nextTick()
    cancelButton.value?.focus()
  },
)
</script>

<template>
  <div
    v-if="open"
    class="dialog-overlay"
    @click.self="emit('cancel')"
    @keydown.esc.stop.prevent="emit('cancel')"
  >
    <section
      class="dialog"
      :class="{ 'is-danger': tone === 'danger' }"
      role="alertdialog"
      aria-modal="true"
      :aria-labelledby="titleId"
      :aria-describedby="message ? messageId : undefined"
    >
      <div class="dialog-head">
        <span class="dialog-icon"><UiIcon :name="icon" :size="34" /></span>
        <div class="dialog-head-copy">
          <h2 :id="titleId">{{ title }}</h2>
          <p v-if="message" :id="messageId">{{ message }}</p>
        </div>
      </div>
      <div v-if="$slots.default" class="dialog-body">
        <slot />
      </div>
      <div class="dialog-actions">
        <button ref="cancelButton" type="button" class="btn btn-quiet" @click="emit('cancel')">
          {{ cancelLabel }}
        </button>
        <button
          type="button"
          class="btn"
          :class="tone === 'danger' ? 'btn-danger' : 'btn-primary'"
          @click="emit('confirm')"
        >
          {{ confirmLabel }}
        </button>
      </div>
    </section>
  </div>
</template>
