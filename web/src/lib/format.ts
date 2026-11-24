// 统一的时间与状态文本格式，避免各页面出现不同的相对时间口径。

function toDate(value: string | Date | null | undefined): Date | null {
  if (!value) return null
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

export function formatDateTime(value: string | Date | null | undefined): string {
  const date = toDate(value)
  return date ? date.toLocaleString('zh-CN', { hour12: false }) : '—'
}

export function formatClock(value: string | Date | null | undefined): string {
  const date = toDate(value)
  return date ? date.toLocaleTimeString('zh-CN', { hour12: false }) : '—'
}

// 相对时间同时支持过去与未来，用于计划执行时间与回执。
export function formatRelative(value: string | Date | null | undefined, now = Date.now()): string {
  const date = toDate(value)
  if (!date) return '—'
  const deltaSeconds = Math.round((date.getTime() - now) / 1000)
  const future = deltaSeconds > 0
  const abs = Math.abs(deltaSeconds)

  if (abs < 10) return '刚刚'
  if (abs < 60) return future ? `${abs} 秒后` : `${abs} 秒前`
  if (abs < 3600) {
    const minutes = Math.round(abs / 60)
    return future ? `${minutes} 分钟后` : `${minutes} 分钟前`
  }
  if (abs < 86_400) {
    const hours = Math.round(abs / 3600)
    return future ? `${hours} 小时后` : `${hours} 小时前`
  }
  if (abs < 604_800) {
    const days = Math.round(abs / 86_400)
    return future ? `${days} 天后` : `${days} 天前`
  }
  return formatDateTime(date)
}

