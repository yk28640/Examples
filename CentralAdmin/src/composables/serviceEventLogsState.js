import { computed, ref } from 'vue'
import { getServiceEventLogs, validateMaximumEntries } from '../api/serviceEventLogs'

export function logLevelKind(level) {
  if (/error|错误|critical|严重/i.test(level)) return 'error'
  if (/warn|警告/i.test(level)) return 'warning'
  return 'info'
}

export function createServiceEventLogsState(fetchLogs = getServiceEventLogs) {
  const selectedSourceId = ref('')
  const states = ref(new Map())
  const state = computed(() => states.value.get(selectedSourceId.value))
  const pageSize = 25
  const filteredItems = computed(() => {
    const snapshot = state.value
    const keyword = snapshot?.keyword.trim().toLocaleLowerCase() || ''
    return (snapshot?.items ?? []).filter((entry) =>
      (snapshot.level === 'all' || logLevelKind(entry.level) === snapshot.level) &&
      (!keyword || Object.values(entry).some((value) => String(value ?? '').toLocaleLowerCase().includes(keyword))))
  })
  const pageCount = computed(() => Math.max(1, Math.ceil(filteredItems.value.length / pageSize)))
  const pageItems = computed(() => filteredItems.value.slice(((state.value?.page ?? 1) - 1) * pageSize,
    (state.value?.page ?? 1) * pageSize))
  let request
  let active = true

  function getState(id) {
    if (!states.value.has(id)) states.value.set(id, {
      items: null, maximumEntries: 100, fetchedMaximumEntries: null, fetchedAtUtc: null,
      loading: false, error: '', keyword: '', level: 'all', page: 1,
    })
    return states.value.get(id)
  }

  function cancel() {
    if (!request) return
    const previous = request
    request = undefined
    getState(previous.sourceId).loading = false
    previous.controller.abort()
  }

  async function load() {
    const sourceId = selectedSourceId.value
    if (!sourceId || !active) return
    cancel()
    const snapshot = getState(sourceId)
    if (!validateMaximumEntries(snapshot.maximumEntries)) {
      snapshot.error = '读取条数必须为 1 到 5000 的整数。'
      return
    }
    const current = { sourceId, controller: new AbortController() }
    request = current
    snapshot.loading = true
    snapshot.error = ''
    try {
      const result = await fetchLogs(sourceId, snapshot.maximumEntries, current.controller.signal)
      if (request !== current || current.controller.signal.aborted) return
      snapshot.items = result.items
      snapshot.fetchedAtUtc = result.fetchedAtUtc
      snapshot.fetchedMaximumEntries = result.maximumEntries
      snapshot.page = 1
    } catch (error) {
      if (request === current && !current.controller.signal.aborted && error.name !== 'AbortError') snapshot.error = error.message
    } finally {
      if (request === current) {
        snapshot.loading = false
        request = undefined
      }
    }
  }

  function selectSource(id) {
    cancel()
    selectedSourceId.value = id
    if (id && getState(id).items === null && !getState(id).error) return load()
  }

  function setMaximumEntries(value) {
    cancel()
    if (state.value) state.value.maximumEntries = value
  }

  function setFilter(name, value) {
    if (!state.value || !['keyword', 'level'].includes(name)) return
    state.value[name] = value
    state.value.page = 1
  }

  function changePage(page) {
    if (state.value && page >= 1 && page <= pageCount.value) state.value.page = page
  }

  function activate() {
    active = true
    if (selectedSourceId.value && state.value?.items === null && !state.value.error && !state.value.loading) return load()
  }

  function deactivate() {
    active = false
    cancel()
  }

  function retainSources(ids) {
    if (!ids.includes(selectedSourceId.value)) selectSource('')
    for (const id of states.value.keys()) if (!ids.includes(id)) states.value.delete(id)
  }

  return { selectedSourceId, state, states, filteredItems, pageItems, pageCount,
    selectSource, setMaximumEntries, setFilter, changePage, load, activate, deactivate, retainSources }
}
