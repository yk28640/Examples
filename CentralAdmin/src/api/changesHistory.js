import { getTagSources } from './tags'

export const HISTORY_REQUEST_TIMEOUT_MS = 300000
export const historyStatuses = {
  Identical: '相同', Different: '修改', Added: '新增', Removed: '删除',
}
export const historyObjectTypes = {
  Block: '程序块', TechnologicalObject: '工艺对象',
}

function invalid(message) {
  throw new Error(`更改历史数据格式异常：${message}`)
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function isDate(value) {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(value)
    && Number.isFinite(Date.parse(value))
}

function validateProject(project) {
  if (!isObject(project) || typeof project.Path !== 'string' || !project.Path.trim()
    || !isDate(project.ModifiedUtc) || !Number.isSafeInteger(project.Length) || project.Length < 0) {
    invalid('工程必须包含有效的 Path、ModifiedUtc 和 Length。')
  }
}

function validateNode(node, version, root = false, depth = 0) {
  if (depth > 100 || !isObject(node) || !Object.hasOwn(historyStatuses, node.ComparisonResult)
    || !Array.isArray(node.Elements)) invalid('对象节点或比较状态无效。')
  if (!root) {
    const type = node.ObjectType ?? (version === 1 ? 'Block' : null)
    if (!Object.hasOwn(historyObjectTypes, type) || (version === 1 && type !== 'Block')) {
      invalid('对象类型无效；版本 2 必须包含 Block 或 TechnologicalObject。')
    }
  }
  for (const field of ['LeftName', 'RightName']) {
    if (node[field] !== null && typeof node[field] !== 'string') invalid('对象名称无效。')
  }
  if (!root && !node.LeftName && !node.RightName) invalid('对象缺少名称。')
  for (const field of ['LatestModifiedDate', 'PreviousModifiedDate']) {
    if (node[field] !== null && !isDate(node[field])) invalid('对象修改时间无效。')
  }
  if (!root && ((!node.LeftName && node.LatestModifiedDate !== null)
    || (!node.RightName && node.PreviousModifiedDate !== null)
    || (node.LeftName && !isDate(node.LatestModifiedDate))
    || (node.RightName && !isDate(node.PreviousModifiedDate)))) invalid('对象名称与修改时间不匹配。')
  node.Elements.forEach((child) => validateNode(child, version, false, depth + 1))
}

export function normalizeChangesHistory(data) {
  if (!isObject(data) || ![1, 2].includes(data.Version) || !isDate(data.GeneratedAtUtc)
    || !Array.isArray(data.Projects) || !Array.isArray(data.Entries)) {
    invalid('接口必须返回版本为 1 或 2 的历史对象及 Projects、Entries 数组。')
  }
  data.Projects.forEach(validateProject)
  const ids = new Set()
  const entries = data.Entries.map((entry) => {
    if (!isObject(entry) || !isDate(entry.ComparedAtUtc) || !Array.isArray(entry.Comparisons)) {
      invalid('历史记录缺少比较时间或 Comparisons 数组。')
    }
    validateProject(entry.PreviousProject)
    validateProject(entry.LatestProject)
    const id = JSON.stringify([entry.PreviousProject.Path, entry.LatestProject.Path])
    if (ids.has(id)) invalid('存在重复的工程比较记录。')
    ids.add(id)
    const blocks = []
    const devices = new Set()
    entry.Comparisons.forEach((comparison) => {
      if (!isObject(comparison) || typeof comparison.Device !== 'string' || !comparison.Device.trim()
        || devices.has(comparison.Device)) invalid('PLC 设备名称缺失或重复。')
      devices.add(comparison.Device)
      validateNode(comparison.Result, data.Version, true)
      function append(node) {
        if (node.Elements.length) {
          node.Elements.forEach(append)
        } else {
          blocks.push({ ...node, ObjectType: node.ObjectType ?? 'Block', Device: comparison.Device })
        }
      }
      comparison.Result.Elements.forEach(append)
    })
    const counts = { Identical: 0, Different: 0, Added: 0, Removed: 0 }
    blocks.forEach((block) => { counts[block.ComparisonResult]++ })
    return { ...entry, id, blocks, counts, changedCount: blocks.length - counts.Identical }
  })
  return { ...data, Entries: entries }
}

function serverError(data, text) {
  const message = typeof data === 'string' ? data : data?.Message || data?.message || data?.detail
  if (typeof message === 'string' && message.trim()) return message.slice(0, 500)
  if (text.trim() && !text.trim().startsWith('<') && data === undefined) return text.trim().slice(0, 500)
  return '服务未返回错误详情。'
}

export async function getChangesHistory(sourceId, signal) {
  const sources = await getTagSources(signal)
  const source = sources.find((item) => item.id === sourceId)
  if (!source) throw new Error('设备来源不存在，请检查 src/config/tagSources.js。')
  const timeout = AbortSignal.timeout(HISTORY_REQUEST_TIMEOUT_MS)
  const combined = signal ? AbortSignal.any([signal, timeout]) : timeout
  let response
  let text
  try {
    response = await fetch(`${source.baseUrl.replace(/\/+$/, '')}/GenerateChangesHistory`, {
      method: 'GET', headers: { Accept: 'application/json' }, signal: combined,
    })
    text = await response.text()
  } catch (error) {
    if (signal?.aborted) throw new DOMException('请求已取消。', 'AbortError')
    if (timeout.aborted || error.name === 'TimeoutError') {
      throw new Error('读取更改历史超时（5 分钟）；后端可能仍在生成，请稍后手动刷新。')
    }
    if (error.name === 'AbortError') throw error
    throw new Error('无法连接历史服务，请检查服务地址、网络及 CORS 跨域设置。')
  }
  let data
  try { data = JSON.parse(text) } catch { /* HTTP 错误可能返回普通文本。 */ }
  if (!response.ok) throw new Error(`读取更改历史失败（HTTP ${response.status}）：${serverError(data, text)}`)
  if (data === undefined) invalid('接口未返回有效 JSON。')
  return {
    sourceId: source.id,
    sourceName: source.name,
    fetchedAtUtc: new Date().toISOString(),
    document: normalizeChangesHistory(data),
  }
}
