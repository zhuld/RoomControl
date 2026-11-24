import type { Schedule, ScheduleKind } from '../types'

// 同一任务在多间教室各生成一个目标；界面按 GroupId 合并成一行。
export interface ScheduleGroup {
  key: string
  groupId: string | null
  name: string
  kind: ScheduleKind
  joinName: string
  roomNames: string[]
  targets: Schedule[]
  enabled: boolean
  nextRunAtUtc: string | null
  weekdayMask: number
  localTime: string
  runAtUtc: string | null
}

export function groupSchedules(schedules: Schedule[]): ScheduleGroup[] {
  const groups = new Map<string, ScheduleGroup>()
  for (const schedule of schedules) {
    const key = schedule.groupId ?? schedule.id
    const existing = groups.get(key)
    if (!existing) {
      groups.set(key, {
        key,
        groupId: schedule.groupId,
        name: schedule.name,
        kind: schedule.kind,
        joinName: schedule.joinName,
        roomNames: [schedule.roomName],
        targets: [schedule],
        enabled: schedule.enabled,
        nextRunAtUtc: schedule.nextRunAtUtc,
        weekdayMask: schedule.weekdayMask,
        localTime: schedule.localTime,
        runAtUtc: schedule.runAtUtc,
      })
      continue
    }

    if (!existing.roomNames.includes(schedule.roomName)) existing.roomNames.push(schedule.roomName)
    existing.targets.push(schedule)
    // 组内任一目标启用即视为启用；下次执行时间取最早的一个。
    existing.enabled = existing.enabled || schedule.enabled
    if (
      schedule.nextRunAtUtc &&
      (!existing.nextRunAtUtc ||
        new Date(schedule.nextRunAtUtc).getTime() < new Date(existing.nextRunAtUtc).getTime())
    ) {
      existing.nextRunAtUtc = schedule.nextRunAtUtc
    }
    if (schedule.kind === 'Once' && schedule.runAtUtc) existing.runAtUtc = schedule.runAtUtc
    existing.weekdayMask |= schedule.weekdayMask
  }

  return [...groups.values()]
}
