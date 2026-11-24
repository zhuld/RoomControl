<script setup lang="ts">
import { ref, watch } from 'vue'
import type { RoomBuildingGroup } from '../../lib/rooms'
import type { Room } from '../../types'
import UiIcon from './UiIcon.vue'

// 操作台与中控配置共用「楼栋 → 楼层 → 教室」三级风箱列表，避免两处各写一套折叠逻辑。
const props = defineProps<{
  groups: RoomBuildingGroup[]
  selectedId: string
  label: string
}>()

const emit = defineEmits<{
  select: [roomId: string]
}>()

defineSlots<{
  status?: (props: { room: Room }) => unknown
  meta?: (props: { room: Room }) => unknown
}>()

// 同一时刻只展开一个楼栋及其中的一个楼层，其余收起。
const openBuilding = ref<string | null>(null)
const openFloor = ref<string | null>(null)

function floorKey(building: string, floor: string) {
  return `${building}\u0000${floor}`
}

function isFloorOpen(building: string, floor: string) {
  return openFloor.value === floorKey(building, floor)
}

function expand(building: string, floor?: string) {
  openBuilding.value = building
  openFloor.value = floor ? floorKey(building, floor) : null
}

function toggleBuilding(building: string) {
  if (openBuilding.value === building) {
    openBuilding.value = null
    openFloor.value = null
    return
  }
  // 展开新楼栋时同时展开它的第一层，直接呈现完整的「楼栋 → 楼层 → 教室」一列。
  const first = props.groups.find((group) => group.building === building)?.floors[0]
  expand(building, first?.floor)
}

function toggleFloor(building: string, floor: string) {
  openFloor.value = openFloor.value === floorKey(building, floor) ? null : floorKey(building, floor)
}

function buildingRoomCount(building: RoomBuildingGroup) {
  return building.floors.reduce((total, floor) => total + floor.rooms.length, 0)
}

function expandToSelection() {
  for (const building of props.groups) {
    for (const floor of building.floors) {
      if (!floor.rooms.some((room) => room.id === props.selectedId)) continue
      expand(building.building, floor.floor)
      return true
    }
  }
  return false
}

function expandFirstGroup() {
  const first = props.groups[0]
  if (!first) return
  expand(first.building, first.floors[0]?.floor)
}

let lastSelection = ''
let initialized = false

// 首次加载展开首组；切换选中教室时展开其所在路径，其余折叠状态由用户控制。
watch(
  () => [props.selectedId, props.groups] as const,
  () => {
    if (!initialized && props.groups.length) {
      initialized = true
      if (!props.selectedId) expandFirstGroup()
    }
    if (props.selectedId && props.selectedId !== lastSelection && expandToSelection()) {
      lastSelection = props.selectedId
    }
  },
  { immediate: true },
)
</script>

<template>
  <nav class="room-list room-tree" :aria-label="label">
    <section v-for="building in groups" :key="building.building" class="room-tree-group">
      <button type="button" class="room-tree-toggle is-building" :aria-expanded="openBuilding === building.building"
        @click="toggleBuilding(building.building)">
        <UiIcon class="room-tree-chevron" :class="{ 'is-open': openBuilding === building.building }"
          name="chevron-right" :size="12" />
        <span class="room-tree-label">{{ building.building }}</span>
        <span class="room-tree-count">{{ buildingRoomCount(building) }}</span>
      </button>

      <div v-if="openBuilding === building.building" class="room-tree-children">
        <section v-for="floor in building.floors" :key="floor.floor" class="room-tree-group">
          <button type="button" class="room-tree-toggle is-floor" :aria-expanded="isFloorOpen(building.building, floor.floor)"
            @click="toggleFloor(building.building, floor.floor)">
            <UiIcon class="room-tree-chevron" :class="{ 'is-open': isFloorOpen(building.building, floor.floor) }"
              name="chevron-right" :size="12" />
            <span class="room-tree-label">{{ floor.floor }}</span>
            <span class="room-tree-count">{{ floor.rooms.length }}</span>
          </button>

          <div v-if="isFloorOpen(building.building, floor.floor)" class="room-tree-children">
            <button v-for="room in floor.rooms" :key="room.id" type="button" class="room-item"
              :class="{ active: room.id === selectedId }" @click="emit('select', room.id)">
              <slot name="status" :room="room" />
              <span class="room-item-copy">
                <strong>{{ room.name }}</strong>
              </span>
              <span class="room-item-count">
                <slot name="meta" :room="room" />
              </span>
            </button>
          </div>
        </section>
      </div>
    </section>

    <p v-if="!groups.length" class="panel-body text-xs muted">没有符合条件的教室。</p>
  </nav>
</template>
