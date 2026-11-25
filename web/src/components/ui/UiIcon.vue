<script setup lang="ts">
import { computed } from 'vue'

// 单线条图标集，保证不同页面使用同一套视觉语义。
const icons: Record<string, string[]> = {
  overview: ['M4 4h7v7H4z', 'M13 4h7v4h-7z', 'M13 10h7v10h-7z', 'M4 13h7v7H4z'],
  operate: ['M4 7h9', 'M17 7h3', 'M4 17h4', 'M12 17h8', 'M13 4v6', 'M8 14v6'],
  automation: ['M12 3a9 9 0 1 0 9 9', 'M12 7v5l3 2', 'M21 3v5h-5'],
  settings: [
    'M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z',
    'M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1 1.55V21a2 2 0 1 1-4 0v-.09a1.7 1.7 0 0 0-1.11-1.55 1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.7 1.7 0 0 0 .34-1.87 1.7 1.7 0 0 0-1.55-1H3a2 2 0 1 1 0-4h.09a1.7 1.7 0 0 0 1.55-1.11 1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.7 1.7 0 0 0 1.87.34H9a1.7 1.7 0 0 0 1-1.55V3a2 2 0 1 1 4 0v.09a1.7 1.7 0 0 0 1 1.55 1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.34 1.87V9a1.7 1.7 0 0 0 1.55 1H21a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.51 1z',
  ],
  audit: ['M8 6h13', 'M8 12h13', 'M8 18h13', 'M3 6h.01', 'M3 12h.01', 'M3 18h.01'],
  menu: ['M4 6h16', 'M4 12h16', 'M4 18h16'],
  search: ['M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z', 'M21 21l-4.3-4.3'],
  refresh: ['M20 11a8 8 0 1 0-.6 4', 'M20 4v7h-7'],
  check: ['M20 6 9 17l-5-5'],
  x: ['M18 6 6 18', 'M6 6l12 12'],
  plus: ['M12 5v14', 'M5 12h14'],
  'chevron-right': ['M9 6l6 6-6 6'],
  'chevrons-left': ['M11 6l-6 6 6 6', 'M18 6l-6 6 6 6'],
  'chevrons-right': ['M13 6l6 6-6 6', 'M6 6l6 6-6 6'],
  drag: ['M9 5h.01', 'M9 12h.01', 'M9 19h.01', 'M15 5h.01', 'M15 12h.01', 'M15 19h.01'],
  logout: ['M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4', 'M16 17l5-5-5-5', 'M21 12H9'],
  room: ['M3 10.5 12 3l9 7.5', 'M5 9.5V21h14V9.5'],
  connection: ['M10 13a5 5 0 0 0 7.5.5l2-2a5 5 0 0 0-7-7l-1 1', 'M14 11a5 5 0 0 0-7.5-.5l-2 2a5 5 0 0 0 7 7l1-1'],
  edit: ['M12 20h9', 'M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z'],
  pause: ['M8 5v14', 'M16 5v14'],
  play: ['M7 4l13 8-13 8z'],
  clock: ['M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z', 'M12 7v5l3 2'],
  shield: ['M12 3 5 6v6c0 4 3 7 7 9 4-2 7-5 7-9V6z'],
  inbox: ['M4 4h16v16H4z', 'M4 13h5l2 3h2l2-3h5'],
  calendar: ['M4 6h16v15H4z', 'M8 3v4', 'M16 3v4', 'M4 11h16'],
  dot: ['M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z'],
  alert: ['M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z', 'M12 9v4', 'M12 17h.01'],
  warning: ['M9 12l2 2 4-4', 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0z'],
}

const props = withDefaults(
  defineProps<{
    name: string
    size?: number
    strokeWidth?: number
  }>(),
  { size: 18, strokeWidth: 1.7 },
)

const paths = computed(() => icons[props.name] ?? icons.dot)
</script>

<template>
  <svg :width="size" :height="size" viewBox="0 0 24 24" fill="none" stroke="currentColor" :stroke-width="strokeWidth"
    stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <path v-for="(path, index) in paths" :key="index" :d="path" />
  </svg>
</template>
