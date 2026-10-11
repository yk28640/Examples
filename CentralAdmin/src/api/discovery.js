const apiBase = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/$/, '')

async function getJson(path, signal) {
  let response
  try {
    response = await fetch(`${apiBase}${path}`, {
      headers: { Accept: 'application/json' },
      signal,
    })
  } catch (error) {
    if (error.name === 'AbortError') throw error
    throw new Error('无法连接扫描历史 API，请检查 AutomationPlatform 是否已启动及 API 地址。')
  }

  if (!response.ok) {
    if (response.status === 404) throw new Error('该扫描记录不存在，可能已被历史清理。')
    throw new Error(`读取扫描历史失败（HTTP ${response.status}）。`)
  }

  return response.json()
}

export function getScans(page, pageSize, signal) {
  return getJson(`/api/discovery/scans?page=${page}&pageSize=${pageSize}`, signal)
}

export function getScan(id, signal) {
  return getJson(`/api/discovery/scans/${encodeURIComponent(id)}`, signal)
}
