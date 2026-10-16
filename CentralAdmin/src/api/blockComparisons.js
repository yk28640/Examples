import { getTagSources } from './tags'

const idPattern = /^[0-9a-f]{32}$/i
const maxPdfBytes = 100 * 1024 * 1024

export function canCompareBlock(block) {
  return block?.ObjectType === 'Block' && block.ComparisonResult === 'Different'
    && !!block.LeftName && !!block.RightName && idPattern.test(block.ExportId ?? '') && !block.ExportError
}

export function validateTask(task) {
  if (!task || !idPattern.test(task.Id ?? '') || !idPattern.test(task.ExportId ?? '')
    || !['Queued', 'Running', 'Succeeded', 'Failed'].includes(task.State)
    || typeof task.Message !== 'string' || !Number.isFinite(Date.parse(task.ExpiresUtc))) {
    throw new Error('比较任务响应格式无效。')
  }
  return task
}

async function request(sourceId, path, init, signal, pdf = false) {
  const sources = await getTagSources(signal)
  const source = sources.find((item) => item.id === sourceId)
  if (!source) throw new Error('设备来源不存在，请重新加载来源。')
  const timeout = AbortSignal.timeout(pdf ? 120000 : 30000)
  const combined = signal ? AbortSignal.any([signal, timeout]) : timeout
  try {
    const response = await fetch(`${source.baseUrl.replace(/\/+$/, '')}/api/block-comparisons${path}`, {
      ...init, headers: { Accept: pdf ? 'application/pdf' : 'application/json', ...init.headers },
      signal: combined, cache: 'no-store',
    })
    if (!response.ok) {
      const text = await response.text()
      let data
      try { data = JSON.parse(text) } catch { /* 服务端也可能返回文本错误。 */ }
      const message = typeof data === 'string' ? data : data?.Message || data?.message
      throw new Error(`程序块比较失败（HTTP ${response.status}）：${message?.slice(0, 500) || '请稍后重试或检查后端日志。'}`)
    }
    if (!pdf) return validateTask(await response.json())
    if (!response.headers.get('Content-Type')?.toLowerCase().startsWith('application/pdf')) {
      throw new Error('后端未返回 PDF 报告。')
    }
    if (Number(response.headers.get('Content-Length')) > maxPdfBytes) throw new Error('PDF 报告过大。')
    const reader = response.body.getReader()
    const chunks = []
    let size = 0
    try {
      while (true) {
        const { value, done } = await reader.read()
        if (done) break
        size += value.byteLength
        if (size > maxPdfBytes) throw new Error('PDF 报告过大。')
        chunks.push(value)
      }
    } finally {
      await reader.cancel().catch(() => {})
      reader.releaseLock()
    }
    if (!size) throw new Error('PDF 报告为空。')
    return new Blob(chunks, { type: 'application/pdf' })
  } catch (error) {
    if (signal?.aborted) throw new DOMException('请求已取消。', 'AbortError')
    if (timeout.aborted) throw new Error('比较请求超时；后端任务可能仍在运行，请稍后重试。')
    if (error instanceof TypeError) throw new Error('无法连接比较服务，请检查网络与 CORS 设置。')
    throw error
  }
}

export function createBlockComparison(sourceId, exportId, signal) {
  if (!idPattern.test(exportId ?? '')) throw new Error('导出标识无效，请刷新历史。')
  return request(sourceId, '', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ExportId: exportId }),
  }, signal)
}

export function getBlockComparison(sourceId, taskId, signal) {
  if (!idPattern.test(taskId ?? '')) throw new Error('任务标识无效。')
  return request(sourceId, `/${taskId}`, { method: 'GET' }, signal)
}

export function getBlockReport(sourceId, taskId, signal) {
  if (!idPattern.test(taskId ?? '')) throw new Error('任务标识无效。')
  return request(sourceId, `/${taskId}/report`, { method: 'GET' }, signal, true)
}

export function waitForPoll(signal, delay = 1500) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new DOMException('请求已取消。', 'AbortError')); return }
    const abort = () => {
      clearTimeout(timer)
      reject(new DOMException('请求已取消。', 'AbortError'))
    }
    const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve() }, delay)
    signal.addEventListener('abort', abort, { once: true })
  })
}
