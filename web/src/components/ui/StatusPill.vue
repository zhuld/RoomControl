<script setup lang="ts">
import type { FeedbackTone } from '../../types'

withDefaults(
  defineProps<{
    tone?: FeedbackTone
    label: string
    dot?: boolean
    pulse?: boolean
  }>(),
  { tone: 'neutral', dot: false, pulse: false },
)
</script>

<template>
  <span class="pill" :class="`pill-${tone}`">
    <span v-if="dot" class="dot" :class="{ 'is-pulse': pulse }"></span>
    {{ label }}
  </span>
</template>
