import type { FeedbackTone, Room } from '../types'

// 教室状态、位置名称与分组排序在总览、操作和配置页共用同一套口径。
export type RoomStatus = Exclude<StatusFilter, 'all'>
export type StatusFilter = (typeof roomStatusFilters)[number]['value']

// 状态筛选的唯一定义来源：筛选按钮、状态标签与色调都从这里取。
export const roomStatusFilters = [
  { value: 'all', label: '全部' },
  { value: 'online', label: '在线', tone: 'success' },
  { value: 'offline', label: '离线', tone: 'danger' },
  { value: 'unconfigured', label: '未接入', tone: 'warning' },
] as const satisfies readonly { value: string; label: string; tone?: FeedbackTone }[]

const UNASSIGNED_BUILDING = '未分配楼栋'
const UNASSIGNED_FLOOR = '未分配楼层'

// 带数字感知的中文排序，保证「2F」排在「10F」之前。
export const collator = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' })

export function buildingOf(room: Room): string {
  return room.building?.trim() || UNASSIGNED_BUILDING
}

export function floorOf(room: Room): string {
  return room.floor?.trim() || UNASSIGNED_FLOOR
}

export function roomStatus(room: Room): RoomStatus {
  if (!room.controller) return 'unconfigured'
  return room.controller.isOnline ? 'online' : 'offline'
}

export function countRooms(rooms: Room[], status: StatusFilter): number {
  return status === 'all' ? rooms.length : rooms.filter((room) => roomStatus(room) === status).length
}

export function roomStatusLabel(room: Room): string {
  const status = roomStatus(room)
  return roomStatusFilters.find((filter) => filter.value === status)?.label ?? status
}

export function roomStatusTone(room: Room): FeedbackTone {
  const status = roomStatus(room)
  const filter = roomStatusFilters.find((item) => item.value === status)
  return filter && 'tone' in filter ? filter.tone : 'neutral'
}

export function sortRoomsByLocation(rooms: Room[]): Room[] {
  return [...rooms].sort(
    (left, right) =>
      collator.compare(buildingOf(left), buildingOf(right)) ||
      collator.compare(floorOf(left), floorOf(right)) ||
      collator.compare(left.name, right.name),
  )
}

export interface RoomFloorGroup {
  floor: string
  rooms: Room[]
}

export interface RoomBuildingGroup {
  building: string
  floors: RoomFloorGroup[]
}

// 按楼栋 → 楼层分组；组内教室保持调用方传入的顺序。
export function groupRoomsByBuilding(rooms: Room[]): RoomBuildingGroup[] {
  const grouped = new Map<string, Map<string, Room[]>>()
  for (const room of rooms) {
    const building = buildingOf(room)
    const floor = floorOf(room)
    const floors = grouped.get(building) ?? new Map<string, Room[]>()
    grouped.set(building, floors)
    const floorRooms = floors.get(floor) ?? []
    floors.set(floor, floorRooms)
    floorRooms.push(room)
  }

  return [...grouped.entries()]
    .sort(([left], [right]) => collator.compare(left, right))
    .map(([building, floors]) => ({
      building,
      floors: [...floors.entries()]
        .sort(([left], [right]) => collator.compare(left, right))
        .map(([floor, floorRooms]) => ({ floor, rooms: floorRooms })),
    }))
}
