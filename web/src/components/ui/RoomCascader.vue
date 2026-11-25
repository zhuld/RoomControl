<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { buildingOf, collator, floorOf } from '../../lib/rooms'
import type { Room } from '../../types'

// 统一的教室选择方式：楼栋 → 楼层 → 教室三级联动。
const props = withDefaults(
  defineProps<{
    rooms: Room[]
    modelValue: string
    allowAll?: boolean
    stacked?: boolean
  }>(),
  { allowAll: false, stacked: false },
)

const emit = defineEmits<{
  'update:modelValue': [roomId: string]
  'levels-change': [levels: { building: string; floor: string }]
}>()

const selectedBuilding = ref('')
const selectedFloor = ref('')
const selectedRoom = ref('')

const buildingOptions = computed(() =>
  [...new Set(props.rooms.map(buildingOf))].sort(collator.compare),
)

const floorOptions = computed(() =>
  selectedBuilding.value
    ? [...new Set(props.rooms.filter((room) => buildingOf(room) === selectedBuilding.value).map(floorOf))].sort(
      collator.compare,
    )
    : [],
)

const roomOptions = computed(() =>
  selectedBuilding.value && selectedFloor.value
    ? props.rooms
      .filter(
        (room) =>
          buildingOf(room) === selectedBuilding.value && floorOf(room) === selectedFloor.value,
      )
      .sort((left, right) => collator.compare(left.name, right.name))
    : [],
)

function syncFromValue() {
  if (!props.modelValue) {
    // 允许「全部」时保留用户已经选择的楼栋/楼层，便于继续筛选。
    if (props.allowAll) return
    selectedBuilding.value = ''
    selectedFloor.value = ''
    selectedRoom.value = ''
    return
  }

  const room = props.rooms.find((item) => item.id === props.modelValue)
  if (!room) {
    selectedRoom.value = ''
    return
  }
  selectedBuilding.value = buildingOf(room)
  selectedFloor.value = floorOf(room)
  selectedRoom.value = room.id
}

watch(() => props.modelValue, syncFromValue, { immediate: true })
watch(
  () => props.rooms,
  () => {
    if (props.modelValue) syncFromValue()
  },
  { immediate: true },
)

function emitLevels() {
  emit('levels-change', {
    building: selectedBuilding.value,
    floor: selectedFloor.value,
  })
}

function emitRoom(roomId: string) {
  emit('update:modelValue', roomId)
  emitLevels()
}

function onBuildingChange(event: Event) {
  selectedBuilding.value = (event.target as HTMLSelectElement).value
  selectedFloor.value = ''
  selectedRoom.value = ''

  if (!selectedBuilding.value) {
    emitRoom('')
    return
  }

  if (props.allowAll) {
    // 楼栋/楼层变化先扩大筛选范围，用户选择具体教室后再收敛。
    emitRoom('')
    return
  }

  const firstFloor = floorOptions.value[0] ?? ''
  selectedFloor.value = firstFloor
  selectedRoom.value = roomOptions.value[0]?.id ?? ''
  emitRoom(selectedRoom.value)
}

function onFloorChange(event: Event) {
  selectedFloor.value = (event.target as HTMLSelectElement).value
  selectedRoom.value = ''

  if (props.allowAll || !selectedFloor.value) {
    emitRoom('')
    return
  }

  selectedRoom.value = roomOptions.value[0]?.id ?? ''
  emitRoom(selectedRoom.value)
}

</script>

<template>
  <div class="room-cascader" :class="{ 'is-stacked': stacked }" role="group" aria-label="教室选择">
    <select name="selectedBuilding" class="select" :value="selectedBuilding" aria-label="楼栋" @change="onBuildingChange">
      <option value="" :disabled="!allowAll">{{ allowAll ? '全部楼栋' : '选择楼栋' }}</option>
      <option v-for="building in buildingOptions" :key="building" :value="building">
        {{ building }}
      </option>
    </select>

    <select name="selectedFloor" class="select" :value="selectedFloor" aria-label="楼层" :disabled="!selectedBuilding"
      @change="onFloorChange">
      <option value="" :disabled="!allowAll">{{ allowAll ? '全部楼层' : '选择楼层' }}</option>
      <option v-for="floor in floorOptions" :key="floor" :value="floor">{{ floor }}</option>
    </select>
  </div>
</template>
