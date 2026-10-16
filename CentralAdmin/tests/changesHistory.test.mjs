import assert from 'node:assert/strict'
import { after, before, test } from 'node:test'
import { createServer } from 'vite'

let server
let api
let createState
let tagSources
const timestamp = '2026-01-01T01:02:03.1234567Z'
const project = (name, day) => ({ Path: `E:\\工程快照\\${name}.ap14`, ModifiedUtc: `2026-01-0${day}T00:00:00Z`, Length: 123 })
const node = (status = 'Different', name = '分组/FB_1') => ({
  LeftName: status === 'Removed' ? null : name,
  RightName: status === 'Added' ? null : name,
  ComparisonResult: status,
  LatestModifiedDate: status === 'Removed' ? null : timestamp,
  PreviousModifiedDate: status === 'Added' ? null : '2025-01-01T00:00:00',
  Elements: [],
})
function fixture(statuses = ['Different', 'Identical', 'Added', 'Removed']) {
  const projects = [project('旧工程', 1), project('新工程', 2)]
  return {
    Version: 1, GeneratedAtUtc: timestamp, Projects: projects,
    Entries: [{ PreviousProject: projects[0], LatestProject: projects[1], ComparedAtUtc: timestamp,
      Comparisons: [{ Device: 'PLC_1', Result: {
        ...node('Different', 'PLC_1'), Elements: statuses.map((status, index) => node(status, `分组/FB_${index}`)),
      } }],
    }],
  }
}
const result = (document) => ({ document, fetchedAtUtc: timestamp })
function deferred() {
  let resolve
  let reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

before(async () => {
  server = await createServer({ server: { middlewareMode: true }, appType: 'custom' })
  api = await server.ssrLoadModule('/src/api/changesHistory.js')
  createState = (await server.ssrLoadModule('/src/composables/changesHistoryState.js')).createChangesHistoryState
  tagSources = (await server.ssrLoadModule('/src/config/tagSources.js')).tagSources
})
after(async () => { await server?.close() })

test('normalization preserves precise timestamps, paths and all four statuses', () => {
  const doc = api.normalizeChangesHistory(fixture())
  assert.equal(doc.Entries.length, 1)
  assert.deepEqual(doc.Entries[0].counts, { Identical: 1, Different: 1, Added: 1, Removed: 1 })
  assert.equal(doc.Entries[0].changedCount, 3)
  assert.equal(doc.Entries[0].blocks[0].LatestModifiedDate, timestamp)
  assert.equal(doc.Entries[0].blocks[0].Device, 'PLC_1')
  assert.equal(doc.Entries[0].blocks[0].LeftName, '分组/FB_0')
})

test('unchanged, empty roots, empty timeline and multiple PLCs remain distinct', () => {
  assert.equal(api.normalizeChangesHistory(fixture(['Identical'])).Entries[0].changedCount, 0)
  assert.equal(api.normalizeChangesHistory(fixture([])).Entries[0].blocks.length, 0)
  const empty = fixture(); empty.Entries = []; empty.Projects = []
  assert.equal(api.normalizeChangesHistory(empty).Entries.length, 0)
  const multiple = fixture(['Added'])
  multiple.Entries[0].Comparisons.push({ Device: 'PLC_2', Result: { ...node('Identical', 'PLC_2'), Elements: [node('Removed')] } })
  const blocks = api.normalizeChangesHistory(multiple).Entries[0].blocks
  assert.deepEqual(blocks.map((block) => block.Device), ['PLC_1', 'PLC_2'])
  assert.equal(blocks[1].LatestModifiedDate, null)
})

test('invalid version, dates, properties, status and duplicate entries are rejected', () => {
  for (const modify of [
    (data) => { data.Version = 3 },
    (data) => { data.GeneratedAtUtc = 'invalid date' },
    (data) => { data.Projects[0].Length = -1 },
    (data) => { delete data.Entries[0].Comparisons },
    (data) => { data.Entries[0].Comparisons[0].Result.Elements[0].ComparisonResult = 'Unknown' },
    (data) => { delete data.Entries[0].Comparisons[0].Result.Elements[0].PreviousModifiedDate },
    (data) => { data.Entries.push(data.Entries[0]) },
  ]) {
    const data = fixture(); modify(data)
    assert.throws(() => api.normalizeChangesHistory(data), /数据格式异常/)
  }
})

test('version 2 distinguishes same-name blocks and technological objects in all statuses', () => {
  const data = fixture()
  data.Version = 2
  const elements = data.Entries[0].Comparisons[0].Result.Elements
  elements.forEach((item) => { item.ObjectType = 'TechnologicalObject' })
  elements.push({ ...elements[0], ObjectType: 'Block' })
  const entry = api.normalizeChangesHistory(data).Entries[0]
  assert.equal(entry.blocks.length, 5)
  assert.equal(entry.changedCount, 4)
  assert.equal(entry.blocks[0].LeftName, entry.blocks[4].LeftName)
  assert.equal(entry.blocks[0].ObjectType, 'TechnologicalObject')
  assert.equal(entry.blocks[4].ObjectType, 'Block')
  assert.deepEqual(entry.counts, { Identical: 1, Different: 2, Added: 1, Removed: 1 })
  assert.equal(api.normalizeChangesHistory(fixture()).Entries[0].blocks[0].ObjectType, 'Block')
  delete elements[0].ObjectType
  assert.throws(() => api.normalizeChangesHistory(data), /对象类型/)
  elements[0].ObjectType = 'Unknown'
  assert.throws(() => api.normalizeChangesHistory(data), /对象类型/)
})

test('API calls selected source GenerateChangesHistory with cancellation signal', async (t) => {
  let url
  let options
  t.mock.method(globalThis, 'fetch', async (address, init) => {
    url = address; options = init
    return new Response(JSON.stringify(fixture()), { status: 200 })
  })
  const response = await api.getChangesHistory(tagSources[0].id, new AbortController().signal)
  assert.equal(url, `${tagSources[0].baseUrl.replace(/\/+$/, '')}/GenerateChangesHistory`)
  assert.equal(options.method, 'GET')
  assert.equal(options.headers.Accept, 'application/json')
  assert.ok(options.signal instanceof AbortSignal)
  assert.equal(response.document.Entries.length, 1)
  await assert.rejects(api.getChangesHistory('not-configured'), /来源不存在/)
})

test('API reports HTTP JSON strings, Web API Message, plain text, malformed JSON and network failure', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch')
  for (const [status, body, message] of [
    [400, JSON.stringify('至少需要两个工程'), '至少需要两个工程'],
    [409, JSON.stringify({ Message: '工程发生变化' }), '工程发生变化'],
    [503, JSON.stringify({ detail: '服务不可用' }), '服务不可用'],
    [503, '服务暂不可用', '服务暂不可用'],
    [404, '<html>not found</html>', 'HTTP 404'],
  ]) {
    fetch.mock.mockImplementation(async () => new Response(body, { status }))
    await assert.rejects(api.getChangesHistory(tagSources[0].id), (error) => error.message.includes(message))
  }
  fetch.mock.mockImplementation(async () => new Response('{bad', { status: 200 }))
  await assert.rejects(api.getChangesHistory(tagSources[0].id), /有效 JSON/)
  fetch.mock.mockImplementation(async () => { throw new TypeError('network') })
  await assert.rejects(api.getChangesHistory(tagSources[0].id), /CORS/)
})

test('API distinguishes caller abort and timeout, including response body abort', async (t) => {
  const abort = new AbortController(); abort.abort()
  await assert.rejects(api.getChangesHistory(tagSources[0].id, abort.signal), { name: 'AbortError' })
  const timeout = new AbortController()
  t.mock.method(AbortSignal, 'timeout', () => timeout.signal)
  t.mock.method(globalThis, 'fetch', async () => ({
    ok: true, status: 200,
    text: async () => { timeout.abort(); throw new DOMException('body aborted', 'AbortError') },
  }))
  await assert.rejects(api.getChangesHistory(tagSources[0].id), /超时/)
})

test('source snapshots are cached; only explicit refresh fetches again', async () => {
  let calls = 0
  const doc = api.normalizeChangesHistory(fixture())
  const state = createState(async () => { calls++; return result(doc) })
  await state.selectSource('a')
  state.state.value.keyword = 'FB'
  await state.selectSource('b')
  await state.selectSource('a')
  assert.equal(calls, 2)
  assert.equal(state.state.value.keyword, 'FB')
  state.deactivate(); await state.activate()
  assert.equal(calls, 2)
  await state.load()
  assert.equal(calls, 3)
})

test('source switch aborts pending requests and late responses cannot overwrite new source', async () => {
  const pending = []
  const state = createState((sourceId, signal) => {
    const job = deferred(); pending.push({ ...job, sourceId, signal }); return job.promise
  })
  const first = state.selectSource('a')
  const second = state.selectSource('b')
  assert.equal(pending[0].signal.aborted, true)
  pending[1].resolve(result(api.normalizeChangesHistory(fixture(['Added'])))); await second
  pending[0].resolve(result(api.normalizeChangesHistory(fixture(['Removed'])))); await first
  assert.equal(state.selectedSourceId.value, 'b')
  assert.equal(state.state.value.document.Entries[0].counts.Added, 1)
  assert.equal(state.states.value.get('a').document, null)
})

test('refresh failures retain successful snapshot and timestamp with visible error state', async () => {
  let fail = false
  const doc = api.normalizeChangesHistory(fixture())
  const state = createState(async () => { if (fail) throw new Error('HTTP 503'); return result(doc) })
  await state.selectSource('a')
  const snapshot = state.state.value.document
  fail = true; await state.load()
  assert.equal(state.state.value.document, snapshot)
  assert.equal(state.state.value.fetchedAtUtc, timestamp)
  assert.equal(state.state.value.error, 'HTTP 503')
  assert.equal(state.state.value.loading, false)
  state.deactivate(); await state.activate()
  assert.equal(state.state.value.error, 'HTTP 503')
})

test('deactivation aborts first load and returning resumes it without accepting late response', async () => {
  const pending = []
  const state = createState((id, signal) => { const job = deferred(); pending.push({ ...job, signal }); return job.promise })
  const first = state.selectSource('a')
  state.deactivate()
  assert.equal(pending[0].signal.aborted, true)
  assert.equal(state.state.value.loading, false)
  const resumed = state.activate()
  pending[0].resolve(result(api.normalizeChangesHistory(fixture(['Removed'])))); await first
  assert.equal(state.state.value.document, null)
  pending[1].resolve(result(api.normalizeChangesHistory(fixture(['Added'])))); await resumed
  assert.equal(state.state.value.document.Entries[0].counts.Added, 1)
})
