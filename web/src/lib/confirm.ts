import { ref } from 'vue'

type ConfirmTone = 'default' | 'danger'

interface ConfirmOptions {
  title?: string
  actionLabel?: string
  tone?: ConfirmTone
}

interface ConfirmState extends Required<ConfirmOptions> {
  message: string
}

// 应用内共用的确认框状态：同一时间只允许一个确认框，重复调用直接视为取消。
export function useConfirmDialog(defaults: ConfirmOptions = {}) {
  const confirmation = ref<ConfirmState | null>(null)
  let resolver: ((confirmed: boolean) => void) | null = null

  function confirmAction(message: string, options: ConfirmOptions = {}): Promise<boolean> {
    if (resolver) return Promise.resolve(false)
    confirmation.value = {
      message,
      title: options.title ?? defaults.title ?? '确认操作',
      actionLabel: options.actionLabel ?? defaults.actionLabel ?? '确认',
      tone: options.tone ?? defaults.tone ?? 'default',
    }
    return new Promise((resolve) => {
      resolver = resolve
    })
  }

  function resolveConfirmation(confirmed: boolean) {
    const resolve = resolver
    resolver = null
    confirmation.value = null
    resolve?.(confirmed)
  }

  return { confirmation, confirmAction, resolveConfirmation }
}
