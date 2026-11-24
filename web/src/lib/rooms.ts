import type { FeedbackTone, Room } from '../types'

// 教室状态、位置名称与分组排序在总览、操作和配置页共用同一套口径。
export type RoomStatus = 'online' | 'offline' | 'unconfigured'
export type StatusFilter = 'all' | RoomStatus

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
  if (roomStatus(room) === 'unconfigured') return '未接入'
  return roomStatus(room) === 'online' ? '在线' : '离线'
}

export function roomStatusTone(room: Room): FeedbackTone {
  if (roomStatus(room) === 'unconfigured') return 'warning'
  return roomStatus(room) === 'online' ? 'success' : 'danger'
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
