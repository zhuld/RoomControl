import type { InjectionKey, Ref } from 'vue'

// 紧凑模式由根组件提供：工作区页头整体隐藏，页头操作传送到顶部栏槽位。
export const compactModeKey: InjectionKey<Ref<boolean>> = Symbol('roomcontrol.compact-mode')
export const actionSlotKey: InjectionKey<Ref<HTMLElement | null>> = Symbol('roomcontrol.action-slot')
