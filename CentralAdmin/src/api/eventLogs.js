const apiBase = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/$/, '')

export async function getOperationLogs(signal) {
  let response
  try {
    response = await fetch(`${apiBase}/api/event-logs/operational`, {
      headers: { Accept: 'application/json' },
      signal,
    })
  } catch (error) {
    if (error.name === 'AbortError') throw error
    throw new Error('无法连接操作日志 API，请检查 AutomationPlatform 是否已启动及 API 地址。')
  }

  if (!response.ok) {
    if (response.status === 404) throw new Error('操作日志接口不存在，请重启 AutomationPlatform 以加载最新的 API。')
    if (response.status === 403) throw new Error('服务器没有读取 TIA Portal 操作日志的权限。')
    if (response.status === 503) throw new Error('TIA Portal 操作日志不可用，请检查服务器上的日志通道。')
    throw new Error(`读取操作日志失败（HTTP ${response.status}）。`)
  }

  return response.json()
}
