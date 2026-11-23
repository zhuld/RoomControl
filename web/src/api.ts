// 局域网断开或后端无响应时，单次请求最多等待 20 秒，避免界面一直停留在“正在登录…”。
const RequestTimeoutMs = 20_000

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
    let message = `请求失败 (${response.status})`
    try {
      const problem = (await response.json()) as { detail?: string; title?: string }
      message = problem.detail || problem.title || message
    } catch {
      // The response may not contain a problem-details body.
    }
    throw new Error(message)
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

