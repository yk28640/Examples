import assert from 'node:assert/strict'
import { after, before, test } from 'node:test'
import { createServer } from 'vite'

let server, api, createState, tagSources
const timestamp = '2026-01-01T01:02:03.1234567Z'
const entry = (index = 1) => ({ TimeCreated: timestamp, User: 'DOMAIN\\user', Computer: 'TIA-PC',
  EventContent: `<script>alert(${index})</script>\n事件内容 ${index}`, EventId: index,
  Level: index % 2 ? '信息' : '警告', Provider: 'Siemens', RecordId: String(index) })
const result = (items = [], maximumEntries = 100) => ({ items, maximumEntries, fetchedAtUtc: timestamp })
function deferred() {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

before(async () => {
  server = await createServer({ server: { middlewareMode: true, hmr: false, ws: false }, appType: 'custom' })
  api = await server.ssrLoadModule('/src/api/serviceEventLogs.js')
  createState = (await server.ssrLoadModule('/src/composables/serviceEventLogsState.js')).createServiceEventLogsState
  tagSources = (await server.ssrLoadModule('/src/config/tagSources.js')).tagSources
})
after(async () => { await server?.close() })

test('all eight fields normalize without changing order, text or timestamp precision', () => {
  const data = api.normalizeServiceEventLogs([entry(2), { ...entry(), TimeCreated: null }])
  assert.deepEqual(Object.keys(data[0]), ['timeCreated', 'user', 'computer', 'eventContent', 'eventId', 'level', 'provider', 'recordId'])
  assert.equal(data[0].eventId, 2)
  assert.equal(data[0].timeCreated, timestamp)
  assert.equal(data[0].eventContent, entry(2).EventContent)
  assert.equal(data[1].timeCreated, null)
  assert.deepEqual(api.normalizeServiceEventLogs([]), [])
  assert.doesNotThrow(() => api.normalizeServiceEventLogs([{ ...entry(), User: '', Level: '', RecordId: '' }]))
})

test('malformed arrays, missing fields, wrong types and dates are rejected', () => {
  for (const data of [null, {}, [null], [{ ...entry(), EventId: '1' }], [{ ...entry(), EventId: -1 }],
    [{ ...entry(), TimeCreated: 'invalid' }], [{ ...entry(), TimeCreated: 0 }], [{ ...entry(), User: null }]]) {
    assert.throws(() => api.normalizeServiceEventLogs(data), /数据格式异常/)
  }
  for (const field of Object.keys(entry())) {
    const item = entry(); delete item[field]
    assert.throws(() => api.normalizeServiceEventLogs([item]), /数据格式异常/)
  }
})

test('GET uses selected source, default limit and boundary limits, and rejects invalid input before fetching', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url, init) => { calls.push({ url, init }); return new Response('[]') })
  for (const limit of [undefined, 1, 100, 5000]) {
    await api.getServiceEventLogs(tagSources[0].id, limit)
    const call = calls.at(-1)
    assert.equal(call.url, `${tagSources[0].baseUrl.replace(/\/+$/, '')}/GetEventLog?maximumEntries=${limit ?? 100}`)
    assert.equal(call.init.method, 'GET')
    assert.equal(call.init.headers.Accept, 'application/json')
    assert.ok(call.init.signal instanceof AbortSignal)
  }
  for (const limit of [0, -1, 5001, 1.5, '100', NaN, null]) {
    await assert.rejects(api.getServiceEventLogs(tagSources[0].id, limit), /整数/)
  }
  await assert.rejects(api.getServiceEventLogs('missing'), /来源不存在/)
  assert.equal(calls.length, 4)
})

test('API identifies HTTP errors, malformed JSON, overflow, network and CORS errors', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch')
  for (const [status, message] of [[400, /条数/], [403, /权限/], [404, /GetEventLog/], [503, /通道/], [500, /HTTP 500/]]) {
    fetch.mock.mockImplementation(async () => new Response('<html>error</html>', { status }))
    await assert.rejects(api.getServiceEventLogs(tagSources[0].id), message)
  }
  fetch.mock.mockImplementation(async () => new Response('{bad'))
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id), /有效 JSON/)
  fetch.mock.mockImplementation(async () => new Response(JSON.stringify([entry(), entry(2)])))
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id, 1), /超过/)
  fetch.mock.mockImplementation(async () => { throw new TypeError('network') })
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id), /CORS/)
})

test('API preserves caller abort and handles timeout during headers and body', async (t) => {
  const aborted = new AbortController(); aborted.abort()
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id, 100, aborted.signal), { name: 'AbortError' })
  const timeout = new AbortController(); timeout.abort(new DOMException('timeout', 'TimeoutError'))
  t.mock.method(AbortSignal, 'timeout', () => timeout.signal)
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id), /超时/)
  const bodyTimeout = new AbortController()
  AbortSignal.timeout.mock.mockImplementation(() => bodyTimeout.signal)
  t.mock.method(globalThis, 'fetch', async () => ({ ok: true, json: async () => {
    bodyTimeout.abort(new DOMException('timeout', 'TimeoutError')); throw bodyTimeout.signal.reason
  } }))
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id), /超时/)
  const caller = new AbortController()
  globalThis.fetch.mock.mockImplementation(async () => ({ ok: true, json: async () => {
    caller.abort(); throw caller.signal.reason
  } }))
  AbortSignal.timeout.mock.mockImplementation(() => new AbortController().signal)
  await assert.rejects(api.getServiceEventLogs(tagSources[0].id, 100, caller.signal), { name: 'AbortError' })
})

test('source switch cancels requests and ignores stale success, failure and finally', async () => {
  const pending = []
  const state = createState((id, limit, signal) => { const job = deferred(); pending.push({ ...job, id, limit, signal }); return job.promise })
  const first = state.selectSource('a')
  const second = state.selectSource('b')
  assert.equal(pending[0].signal.aborted, true)
  pending[0].reject(new Error('late failure')); await first
  assert.equal(state.state.value.loading, true)
  assert.equal(state.state.value.error, '')
  pending[1].resolve(result([{ eventId: 2 }])); await second
  assert.equal(state.state.value.items[0].eventId, 2)
  assert.equal(state.states.value.get('a').items, null)
  const third = state.selectSource('a')
  state.deactivate()
  pending[2].resolve(result([{ eventId: 3 }])); await third
  assert.equal(state.state.value.items, null)
  assert.equal(state.state.value.loading, false)
  const resumed = state.activate()
  pending[3].resolve(result([])); await resumed
  assert.deepEqual(state.state.value.items, [])
})

test('source settings and empty successful snapshots are cached, refresh failure retains data and timestamp', async () => {
  let calls = 0, fail = false
  const state = createState(async (id, limit) => { calls++; if (fail) throw new Error('HTTP 503'); return result([], limit) })
  await state.selectSource('a')
  state.setMaximumEntries(5000)
  await state.load()
  state.setFilter('keyword', 'Siemens')
  await state.selectSource('b')
  assert.equal(state.state.value.maximumEntries, 100)
  await state.selectSource('a')
  assert.equal(calls, 3)
  assert.equal(state.state.value.maximumEntries, 5000)
  assert.equal(state.state.value.keyword, 'Siemens')
  fail = true; await state.load()
  assert.deepEqual(state.state.value.items, [])
  assert.equal(state.state.value.fetchedAtUtc, timestamp)
  assert.equal(state.state.value.fetchedMaximumEntries, 5000)
  assert.equal(state.state.value.error, 'HTTP 503')
  state.deactivate(); await state.activate()
  assert.equal(calls, 4)
  for (const limit of [0, -1, 5001, 1.5, null]) { state.setMaximumEntries(limit); await state.load() }
  assert.equal(calls, 4)
})

test('filtering searches all fields, level filtering and pagination reset correctly', async () => {
  const items = api.normalizeServiceEventLogs(Array.from({ length: 60 }, (_, i) => entry(i + 1)))
  const state = createState(async () => result(items))
  await state.selectSource('a')
  assert.equal(state.pageCount.value, 3)
  state.changePage(3)
  assert.equal(state.pageItems.value.length, 10)
  state.setFilter('level', 'warning')
  assert.equal(state.state.value.page, 1)
  assert.equal(state.filteredItems.value.length, 30)
  state.setFilter('keyword', '事件内容 60')
  assert.equal(state.filteredItems.value.length, 1)
  assert.equal(state.pageItems.value[0].eventId, 60)
  state.setFilter('keyword', 'no-match')
  assert.equal(state.filteredItems.value.length, 0)
  assert.equal(state.pageCount.value, 1)
})

test('editing limit cancels old read and stale response does not update snapshot', async () => {
  const job = deferred()
  const state = createState(() => job.promise)
  const pending = state.selectSource('a')
  state.setMaximumEntries(1)
  job.resolve(result([{ eventId: 100 }])); await pending
  assert.equal(state.state.value.items, null)
  assert.equal(state.state.value.maximumEntries, 1)
  state.retainSources([])
  assert.equal(state.selectedSourceId.value, '')
  assert.equal(state.states.value.size, 0)
})
