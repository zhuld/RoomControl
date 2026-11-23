<script setup lang="ts">
import UiIcon from './UiIcon.vue'

withDefaults(
  defineProps<{
    icon?: string
    title: string
    description?: string
  }>(),
  { icon: 'inbox', description: '' },
)
</script>

<template>
  <div class="empty-state">
    <span class="empty-icon"><UiIcon :name="icon" :size="22" /></span>
    <h3>{{ title }}</h3>
    <p v-if="description">{{ description }}</p>
    <slot />
  </div>
</template>
