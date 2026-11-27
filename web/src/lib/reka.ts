import { createDateSegmentTranslations } from 'reka-ui'

// Reka 只提供行为，内置的辅助文案需要由应用本地化；未列出的键继续使用英文默认值。
export const rekaTranslations = {
  ...createDateSegmentTranslations('zh-CN'),
  calendarLabel: '选择日期',
  calendarPrev: '上一页',
  calendarNext: '下一页',
  paginationLabel: '分页',
  paginationFirst: '第一页',
  paginationPrev: '上一页',
  paginationNext: '下一页',
  paginationLast: '最后一页',
  paginationPage: '第 {page} 页',
  toastLabel: '通知',
  toastViewportLabel: '通知（{hotkey}）',
  comboboxTrigger: '展开选项',
  numberFieldRoleDescription: '数字输入框',
  numberFieldIncrement: '增加',
  numberFieldDecrement: '减少',
}
