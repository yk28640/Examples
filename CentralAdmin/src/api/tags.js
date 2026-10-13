import { tagSources } from '../config/tagSources'

const fields = ['Device', 'Group', 'Table', 'Name', 'DataType', 'Address']

async function getJson(url, signal) {
  let response
  try {
    response = await fetch(url, {
      headers: { Accept: 'application/json' },
      signal: AbortSignal.any([signal, AbortSignal.timeout(75000)]),
    })
  } catch (error) {
    if (error.name === 'AbortError') throw error
    if (error.name === 'TimeoutError') throw new Error('读取设备来源超时，请检查 PLC 标签服务是否可用。')
    throw new Error('无法连接设备来源，请检查网络及 PLC 标签服务的跨域设置。')
  }

  let data
  try {
    data = await response.json()
  } catch (error) {
    if (error.name === 'AbortError') throw error
    if (error.name === 'TimeoutError') throw new Error('读取设备来源超时，请检查 PLC 标签服务是否可用。')
    if (!response.ok) throw new Error(`读取设备来源失败（HTTP ${response.status}）。`)
    throw new Error('设备来源数据格式异常：接口未返回有效 JSON。')
  }
  if (!response.ok) {
    throw new Error(typeof data?.detail === 'string' && data.detail
      ? data.detail
      : `读取设备来源失败（HTTP ${response.status}）。`)
  }
  return data
}

export async function getTagSources(signal) {
  if (!Array.isArray(tagSources) || tagSources.some((source) => {
    if (!source || typeof source.id !== 'string' || !source.id ||
      typeof source.name !== 'string' || !source.name || typeof source.baseUrl !== 'string') return true
    let url
    try {
      url = new URL(source.baseUrl)
    } catch {
      return true
    }
    return !['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash
  }) || new Set(tagSources.map((source) => source.id.toLowerCase())).size !== tagSources.length) {
    throw new Error('设备来源配置格式异常：每个来源必须具有唯一 id、name 和有效 baseUrl。')
  }
  if (signal?.aborted) throw new DOMException('The operation was aborted.', 'AbortError')
  return tagSources
}

export async function getTags(sourceId, signal) {
  const source = tagSources.find((item) => item.id === sourceId)
  if (!source) throw new Error('设备来源配置不存在，请重新加载来源列表。')
  const url = `${source.baseUrl.replace(/\/$/, '')}/getTags`
  const data = await getJson(url, signal)
  if (!Array.isArray(data) || data.some((tag) =>
    !tag || typeof tag !== 'object' || fields.some((field) => typeof tag[field] !== 'string')
  )) {
    throw new Error('PLC 变量数据格式异常：接口必须返回包含六个字符串字段的变量数组。')
  }
  return {
    sourceId: source.id,
    sourceName: source.name,
    fetchedAtUtc: new Date().toISOString(),
    items: data,
  }
}
