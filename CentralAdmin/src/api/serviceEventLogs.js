import { getTagSources } from './tags'

export const EVENT_LOG_REQUEST_TIMEOUT_MS = 75000
const stringFields = ['User', 'Computer', 'EventContent', 'Level', 'Provider', 'RecordId']

export function validateMaximumEntries(value) {
  return Number.isInteger(value) && value >= 1 && value <= 5000
}

export function normalizeServiceEventLogs(data) {
  if (!Array.isArray(data) || data.some((entry) =>
    !entry || typeof entry !== 'object' || stringFields.some((field) => typeof entry[field] !== 'string') ||
    !Number.isSafeInteger(entry.EventId) || entry.EventId < 0 ||
    !(entry.TimeCreated === null || (typeof entry.TimeCreated === 'string' &&
      /^\d{4}-\d{2}-\d{2}T/.test(entry.TimeCreated) && Number.isFinite(Date.parse(entry.TimeCreated))))
  )) {
    throw new Error('ServiceV14 日志数据格式异常：需要包含完整八个字段的日志数组。')
  }
  return data.map((entry) => ({
    timeCreated: entry.TimeCreated, user: entry.User, computer: entry.Computer,
    eventContent: entry.EventContent, eventId: entry.EventId, level: entry.Level,
    provider: entry.Provider, recordId: entry.RecordId,
  }))
}

export async function getServiceEventLogs(sourceId, maximumEntries = 100, signal) {
  if (!validateMaximumEntries(maximumEntries)) throw new Error('读取条数必须为 1 到 5000 的整数。')
  const sources = await getTagSources(signal)
  const source = sources.find((item) => item.id === sourceId)
  if (!source) throw new Error('日志设备来源不存在，请重新加载来源。')
  const requestSignal = AbortSignal.any([
    ...(signal ? [signal] : []), AbortSignal.timeout(EVENT_LOG_REQUEST_TIMEOUT_MS),
  ])
  try {
    requestSignal.throwIfAborted()
    const response = await fetch(`${source.baseUrl.replace(/\/+$/, '')}/GetEventLog?maximumEntries=${maximumEntries}`, {
      method: 'GET', headers: { Accept: 'application/json' }, signal: requestSignal,
    })
    if (!response.ok) {
      const messages = {
        400: '读取条数无效，ServiceV14 仅接受 1 到 5000 的整数。',
        403: 'ServiceV14 运行账户没有读取 TIA Portal 操作日志的权限。',
        404: '所选来源没有 GetEventLog 接口，请部署并重启新版 ServiceV14。',
        503: 'ServiceV14 日志不可用，请检查来源机器上的 SiemensAG-Automation-TIAPortal/Operational 通道。',
      }
      throw new Error(messages[response.status] || `读取 ServiceV14 日志失败（HTTP ${response.status}）。`)
    }
    let data
    try {
      data = await response.json()
    } catch (error) {
      if (requestSignal.aborted) throw error
      throw new Error('ServiceV14 日志数据格式异常：接口未返回有效 JSON。')
    }
    requestSignal.throwIfAborted()
    const items = normalizeServiceEventLogs(data)
    if (items.length > maximumEntries) throw new Error('ServiceV14 日志数据格式异常：返回条数超过请求上限。')
    return { sourceId: source.id, items, fetchedAtUtc: new Date().toISOString(), maximumEntries }
  } catch (error) {
    if (signal?.aborted) throw signal.reason
    if (requestSignal.aborted || error.name === 'TimeoutError') {
      throw new Error('读取 ServiceV14 日志超时，请检查来源服务后手动重试。')
    }
    if (error.name === 'AbortError') throw error
    if (error instanceof TypeError) throw new Error('无法连接 ServiceV14，请检查网络、来源地址、CORS 跨域设置及 HTTPS 混合内容限制。')
    throw error
  }
}
