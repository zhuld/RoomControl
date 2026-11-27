// 局域网断开或后端无响应时，单次请求最多等待 20 秒，避免界面一直停留在“正在登录…”。
const RequestTimeoutMs = 20_000

// 状态码说明只维护一份：message 用于错误提示，label 用于操作记录等紧凑位置。
const statusDescriptions: Record<number, { label: string; message?: string }> = {
  200: { label: '成功' , message: '请求已成功处理' },
  201: { label: '已创建' , message: '资源已成功创建' },
  204: { label: '成功' , message: '请求已成功处理，但无返回内容' },
  400: { label: '参数有误', message: '提交的内容有误，请检查后重试' },
  401: { label: '未登录', message: '未登录或登录状态已失效，请重新登录' },
  403: { label: '没有权限', message: '没有执行该操作的权限' },
  404: { label: '不存在', message: '请求的数据不存在，可能已被删除' },
  405: { label: '不支持的操作', message: '服务器不支持该操作' },
  408: { label: '请求超时', message: '请求超时，请检查网络后重试' },
  409: { label: '状态冲突', message: '数据已发生变化，请刷新后重试' },
  413: { label: '内容过大', message: '提交的内容过大' },
  415: { label: '格式不支持', message: '提交的数据格式不受支持' },
  422: { label: '无法处理', message: '提交的数据无法处理，请检查后重试' },
  429: { label: '操作过于频繁', message: '操作过于频繁，请稍后再试' },
  500: { label: '服务器错误', message: '服务器内部错误，请稍后重试' },
  502: { label: '网关错误', message: '服务器网关异常，请稍后重试' },
  503: { label: '服务不可用', message: '服务暂时不可用，请稍后重试' },
  504: { label: '响应超时', message: '服务器响应超时，请稍后重试' },
}

export class ApiError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

function describeStatus(status: number): string {
  const known = statusDescriptions[status]?.message
  if (known) {
    return `${known}（${status}）。`
  }
  if (status >= 500) {
    return `服务器处理请求失败（${status}），请稍后重试。`
  }
  return `请求无法完成（${status}）。`
}

export function describeStatusLabel(status: number): string {
  const known = statusDescriptions[status]
  if (known) {
    return known.label
  }
  if (status >= 500) {
    return '服务器错误'
  }
  return status >= 400 ? '请求失败' : '成功'
}

// 所有 API 请求携带同源会话 Cookie，并将 ProblemDetails 转为可显示错误。
export async function request(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      headers,
      credentials: 'include',
      // 调用方传入 signal 时以调用方为准。
      signal: init.signal ?? AbortSignal.timeout(RequestTimeoutMs),
    })
  } catch (error) {
    throw new Error(toRequestFailureMessage(error))
  }
  if (!response.ok) {
    let message = describeStatus(response.status)
    try {
      // 只使用后端的中文 detail；title 是英文规范短语，不直接展示给用户。
      const problem = (await response.json()) as { detail?: string }
      message = problem.detail || message
    } catch {
      // The response may not contain a problem-details body.
    }
    throw new ApiError(message, response.status)
  }
  return response
}

export async function readJson<T>(path: string): Promise<T> {
  return request(path).then((response) => response.json() as Promise<T>)
}

export function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '操作失败，请重试。'
}

function toRequestFailureMessage(error: unknown): string {
  if (error instanceof DOMException && error.name === 'TimeoutError') {
    return '请求超时，请检查网络连接后重试。'
  }
  return '无法连接服务器，请确认服务已启动且网络可达。'
}

