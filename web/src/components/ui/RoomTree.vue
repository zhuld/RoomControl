<script setup lang="ts">
import { TreeItem, TreeRoot } from 'reka-ui'
import { computed, ref, watch } from 'vue'
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

interface TreeNode {
  key: string
  kind: 'building' | 'floor' | 'room'
  level: number
  label: string
  count?: number
  room?: Room
  building?: string
  floor?: string
  children: TreeNode[]
}

// 同一时刻只展开一个楼栋及其中的一个楼层，其余收起。
const openBuilding = ref<string | null>(null)
const openFloor = ref<string | null>(null)

function buildingKey(building: string) {
  return `b:${building}`
}

function floorKey(building: string, floor: string) {
  return `f:${building}\u0000${floor}`
}

function roomKey(roomId: string) {
  return `r:${roomId}`
}

function buildingRoomCount(building: RoomBuildingGroup) {
  return building.floors.reduce((total, floor) => total + floor.rooms.length, 0)
}

const tree = computed<TreeNode[]>(() =>
  props.groups.map<TreeNode>((building) => ({
    key: buildingKey(building.building),
    kind: 'building',
    level: 1,
    label: building.building,
    count: buildingRoomCount(building),
    building: building.building,
    children: building.floors.map<TreeNode>((floor) => ({
      key: floorKey(building.building, floor.floor),
      kind: 'floor',
      level: 2,
      label: floor.floor,
      count: floor.rooms.length,
      building: building.building,
      floor: floor.floor,
      children: floor.rooms.map<TreeNode>((room) => ({
        key: roomKey(room.id),
        kind: 'room',
        level: 3,
        label: room.name,
        room,
        children: [],
      })),
    })),
  })),
)

function getKey(node: TreeNode) {
  return node.key
}

function getChildren(node: TreeNode) {
  // 叶子节点返回 undefined，避免 Reka 给教室标记 aria-expanded。
  return node.children.length ? node.children : undefined
}

const expandedKeys = computed(() => {
  if (!openBuilding.value) return []
  const keys = [buildingKey(openBuilding.value)]
  if (openFloor.value) keys.push(openFloor.value)
  return keys
})

const visibleNodes = computed<TreeNode[]>(() => {
  const nodes: TreeNode[] = []
  for (const building of tree.value) {
    nodes.push(building)
    if (openBuilding.value !== building.label) continue
    for (const floor of building.children) {
      nodes.push(floor)
      if (openFloor.value === floor.key) nodes.push(...floor.children)
    }
  }
  return nodes
})

const selectedNode = computed<TreeNode | undefined>(() => {
  if (!props.selectedId) return undefined
  for (const building of tree.value) {
    for (const floor of building.children) {
      for (const room of floor.children) {
        if (room.room?.id === props.selectedId) return room
      }
    }
  }
  return undefined
})

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

function toggleNode(node: TreeNode) {
  if (node.kind === 'building' && node.building) {
    toggleBuilding(node.building)
  } else if (node.kind === 'floor' && node.building && node.floor) {
    toggleFloor(node.building, node.floor)
  }
}

function handleNodeSelect(node: TreeNode, event: Event) {
  if (node.kind === 'room') return
  // 分支节点只负责展开/收起，不写入 Reka 的选中值。
  event.preventDefault()
  if (event instanceof KeyboardEvent) toggleNode(node)
}

function handleSelection(value: unknown) {
  const node = value as TreeNode | undefined
  if (node?.kind === 'room' && node.room) emit('select', node.room.id)
}

function handleExpandedChange(keys: string[]) {
  const current = new Set(expandedKeys.value)
  const next = new Set(keys)
  const added = keys.find((key) => !current.has(key))
  const removed = [...current].find((key) => !next.has(key))

  if (added) {
    if (added.startsWith('b:')) {
      const building = added.slice(2)
      const first = props.groups.find((group) => group.building === building)?.floors[0]
      openBuilding.value = building
      openFloor.value = first ? floorKey(building, first.floor) : null
    } else if (added.startsWith('f:')) {
      const [building, floor] = added.slice(2).split('\u0000')
      if (building && floor) {
        openBuilding.value = building
        openFloor.value = added
      }
    }
    return
  }

  if (!removed) return
  if (removed.startsWith('b:')) {
    openBuilding.value = null
    openFloor.value = null
  } else if (removed.startsWith('f:')) {
    openFloor.value = null
  }
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
  <TreeRoot v-if="groups.length" as="div" class="room-list room-tree" :aria-label="label" :items="tree"
    :get-key="getKey" :get-children="getChildren" :expanded="expandedKeys" :model-value="selectedNode"
    @update:expanded="handleExpandedChange" @update:model-value="handleSelection">
    <TreeItem v-for="node in visibleNodes" :key="node.key" :value="node" :level="node.level" as-child
      @select="(event: Event) => handleNodeSelect(node, event)">
      <button v-if="node.kind === 'room'" type="button" class="room-item"
        :class="{ active: node.room?.id === selectedId }">
        <slot name="status" :room="node.room!" />
        <span class="room-item-copy">
          <strong>{{ node.label }}</strong>
        </span>
        <span class="room-item-count">
          <slot name="meta" :room="node.room!" />
        </span>
      </button>
      <button v-else type="button" class="room-tree-toggle"
        :class="node.kind === 'building' ? 'is-building' : 'is-floor'">
        <UiIcon class="room-tree-chevron" :class="{ 'is-open': expandedKeys.includes(node.key) }" name="chevron-right"
          :size="12" />
        <span class="room-tree-label">{{ node.label }}</span>
        <span class="room-tree-count">{{ node.count }}</span>
      </button>
    </TreeItem>
  </TreeRoot>

  <p v-else class="panel-body text-xs muted">没有符合条件的教室。</p>
</template>
