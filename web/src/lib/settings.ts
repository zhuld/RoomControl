import type { SettingsSection, UserRole } from '../types'

// 配置页步骤的角色权限：外观对三种角色开放，自动任务对操作员开放，其余仅管理员。
export function canAccessSettingsSection(
  section: SettingsSection,
  role: UserRole | undefined,
): boolean {
  if (role === 'admin') return true
  if (role === 'operator') return section === 'automation' || section === 'appearance'
  return section === 'appearance'
}

export function defaultSettingsSection(role: UserRole | undefined): SettingsSection {
  if (role === 'admin') return 'branding'
  return role === 'operator' ? 'automation' : 'appearance'
}
