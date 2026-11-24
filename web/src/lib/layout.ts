import type { InjectionKey, Ref } from 'vue'

// 各工作区的页头操作统一传送到顶部栏槽位。
export const actionSlotKey: InjectionKey<Ref<HTMLElement | null>> = Symbol('roomcontrol.action-slot')
