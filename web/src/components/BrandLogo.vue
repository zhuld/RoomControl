<script setup lang="ts">
import { computed } from 'vue'

// 无上传 Logo 时，从站点名称生成简短文字标记作为回退展示。
const props = defineProps<{
  logoDataUrl?: string | null
  label: string
}>()

const initials = computed(() => {
  const label = props.label.trim()
  if (!label) return 'RC'
  const words = label.split(/\s+/).filter(Boolean)
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase()
  return label.slice(0, 2).toUpperCase()
})
</script>

<template>
  <img v-if="logoDataUrl" class="brand-logo" :src="logoDataUrl" :alt="`${label} 标志`" />
  <div v-else class="brand-mark">{{ initials }}</div>
</template>
