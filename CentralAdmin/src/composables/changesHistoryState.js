import { computed, ref } from 'vue'
import { getChangesHistory } from '../api/changesHistory'

export function createChangesHistoryState(fetchHistory = getChangesHistory) {
  const selectedSourceId = ref('')
  const states = ref(new Map())
  const state = computed(() => states.value.get(selectedSourceId.value))
  let request
  let active = true

  function getState(id) {
    if (!states.value.has(id)) {
      states.value.set(id, {
        document: null, fetchedAtUtc: null, loading: false, error: '',
        selectedEntryId: '', onlyChanges: true, keyword: '', objectType: '', page: 1,
      })
    }
    return states.value.get(id)
  }

  function cancel() {
    if (!request) return
    const previous = request
    request = undefined
    getState(previous.sourceId).loading = false
    previous.controller.abort()
  }

  async function load(sourceId = selectedSourceId.value) {
    if (!sourceId || !active) return
    cancel()
    const snapshot = getState(sourceId)
    const current = { sourceId, controller: new AbortController() }
    request = current
    snapshot.loading = true
    snapshot.error = ''
    try {
      const result = await fetchHistory(sourceId, current.controller.signal)
      if (request !== current || current.controller.signal.aborted) return
      snapshot.document = result.document
      snapshot.fetchedAtUtc = result.fetchedAtUtc
      if (!result.document.Entries.some((entry) => entry.id === snapshot.selectedEntryId)) {
        snapshot.selectedEntryId = result.document.Entries.at(-1)?.id ?? ''
      }
      snapshot.page = 1
    } catch (error) {
      if (request === current && error.name !== 'AbortError') snapshot.error = error.message
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
    if (id && !getState(id).document && !getState(id).error) return load(id)
  }

  function deactivate() {
    active = false
    cancel()
  }

  function activate() {
    active = true
    const snapshot = state.value
    if (selectedSourceId.value && !snapshot?.document && !snapshot?.error && !snapshot?.loading) return load()
  }

  function retainSources(ids) {
    for (const id of states.value.keys()) {
      if (!ids.includes(id)) states.value.delete(id)
    }
  }

  return { selectedSourceId, state, states, selectSource, load, activate, deactivate, retainSources }
}
