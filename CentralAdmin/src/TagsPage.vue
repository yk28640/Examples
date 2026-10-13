<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { getTags, getTagSources } from './api/tags'

const sources = ref([])
const sourcesLoading = ref(false)
const sourcesError = ref('')
const selectedSourceId = ref('')
const sourceStates = ref(new Map())
const selectedSource = computed(() => sources.value.find((source) => source.id === selectedSourceId.value))
const sourceState = computed(() => sourceStates.value.get(selectedSourceId.value))
const tags = computed(() => sourceState.value?.items ?? [])
const loading = computed(() => sourceState.value?.loading ?? false)
const error = computed(() => sourceState.value?.error ?? '')
const updatedAt = computed(() => {
  const value = sourceState.value?.fetchedAtUtc
  return value ? new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }).format(new Date(value)) : '尚未读取'
})
const sourceStatus = computed(() => loading.value ? '正在读取' : error.value ? '读取失败'
  : sourceState.value?.hasSnapshot ? '已读取' : '尚未读取')
const keyword = ref('')
const device = ref(null)
const group = ref(null)
const table = ref(null)
const dataType = ref(null)
const page = ref(1)
const pageSize = 25
let sourcesController
let tagRequest

function options(field) {
  return computed(() => [...new Set(tags.value.map((tag) => tag[field]))].sort((a, b) => a.localeCompare(b, 'zh-CN')))
}

const devices = options('Device')
const groups = options('Group')
const tables = options('Table')
const dataTypes = options('DataType')
const filteredTags = computed(() => {
  const search = keyword.value.trim().toLocaleLowerCase()
  return tags.value.filter((tag) =>
    (device.value === null || tag.Device === device.value) &&
    (group.value === null || tag.Group === group.value) &&
    (table.value === null || tag.Table === table.value) &&
    (dataType.value === null || tag.DataType === dataType.value) &&
    (!search || [tag.Device, tag.Group, tag.Table, tag.Name, tag.DataType, tag.Address]
      .some((value) => value.toLocaleLowerCase().includes(search)))
  )
})
const pageCount = computed(() => Math.max(1, Math.ceil(filteredTags.value.length / pageSize)))
const visibleTags = computed(() => filteredTags.value.slice((page.value - 1) * pageSize, page.value * pageSize))
watch(filteredTags, () => { page.value = 1 })

function resetFilters() {
  keyword.value = ''
  device.value = null
  group.value = null
  table.value = null
  dataType.value = null
}

function cancelTagRequest() {
  if (!tagRequest) return
  tagRequest.controller.abort()
  sourceStates.value.get(tagRequest.sourceId).loading = false
  tagRequest = undefined
}

function getSourceState(sourceId) {
  if (!sourceStates.value.has(sourceId)) {
    sourceStates.value.set(sourceId, {
      items: [], fetchedAtUtc: null, loading: false, error: '', hasSnapshot: false,
    })
  }
  return sourceStates.value.get(sourceId)
}

async function loadTags(sourceId = selectedSourceId.value) {
  cancelTagRequest()
  const state = getSourceState(sourceId)
  const request = { sourceId, controller: new AbortController() }
  tagRequest = request
  state.loading = true
  state.error = ''
  try {
    const result = await getTags(sourceId, request.controller.signal)
    if (tagRequest !== request || request.controller.signal.aborted) return
    state.items = result.items
    state.fetchedAtUtc = result.fetchedAtUtc
    state.hasSnapshot = true
  } catch (failure) {
    if (tagRequest === request && failure.name !== 'AbortError') {
      state.error = failure.message
    }
  } finally {
    if (tagRequest === request) {
      state.loading = false
      tagRequest = undefined
    }
  }
}

watch(selectedSourceId, (sourceId) => {
  cancelTagRequest()
  resetFilters()
  page.value = 1
  if (sourceId && !getSourceState(sourceId).hasSnapshot) loadTags(sourceId)
})

async function loadSources() {
  sourcesController?.abort()
  const request = new AbortController()
  sourcesController = request
  sourcesLoading.value = true
  sourcesError.value = ''
  try {
    const result = await getTagSources(request.signal)
    if (sourcesController !== request || request.signal.aborted) return
    sources.value = result
    if (!result.some((source) => source.id === selectedSourceId.value)) {
      cancelTagRequest()
      selectedSourceId.value = result[0]?.id ?? ''
    }
    for (const sourceId of sourceStates.value.keys()) {
      if (!result.some((source) => source.id === sourceId)) sourceStates.value.delete(sourceId)
    }
  } catch (failure) {
    if (sourcesController === request && failure.name !== 'AbortError') {
      sourcesError.value = failure.message
      sources.value = []
      selectedSourceId.value = ''
    }
  } finally {
    if (sourcesController === request) sourcesLoading.value = false
  }
}

onMounted(loadSources)
onUnmounted(() => {
  sourcesController?.abort()
  cancelTagRequest()
})
</script>

<template>
  <div id="plc-tags" class="dashboard">
    <div class="title-row">
      <div>
        <div class="eyebrow"><span class="eyebrow-line"></span> PLC TAG INVENTORY <span class="eyebrow-index">/ 03</span></div>
        <h1>PLC 变量<span class="title-period">.</span></h1>
        <p class="subtitle">浏览设备变量定义、数据类型与地址；此接口不提供实时变量值。</p>
      </div>
      <button class="refresh-button" type="button" :disabled="loading || sourcesLoading || !selectedSource" @click="loadTags()">
        <span aria-hidden="true">↻</span> {{ loading ? '刷新中…' : '刷新当前来源' }}
      </button>
    </div>

    <section class="panel tag-source-panel" aria-label="设备来源">
      <div class="tag-source-controls">
        <label for="tag-source">设备来源
          <select id="tag-source" v-model="selectedSourceId" :disabled="sourcesLoading || sources.length === 0">
            <option v-if="sources.length === 0" value="">{{ sourcesLoading ? '正在加载来源…' : '暂无可用来源' }}</option>
            <option v-for="source in sources" :key="source.id" :value="source.id">{{ source.name }} · {{ source.id }}</option>
          </select>
        </label>
        <div class="tag-source-status" :class="{ 'source-error': error }" role="status">
          <span>{{ selectedSource ? sourceStatus : '未选择来源' }}</span>
          <small>上次成功读取：{{ updatedAt }}</small>
        </div>
        <button class="refresh-button" type="button" :disabled="sourcesLoading" @click="loadSources">
          {{ sourcesLoading ? '加载中…' : '重新加载来源' }}
        </button>
      </div>
      <div v-if="sourcesError" class="notice error" role="alert">{{ sourcesError }} <button type="button" @click="loadSources">重试</button></div>
      <div v-else-if="!sourcesLoading && sources.length === 0" class="notice warning" role="status">暂无启用的设备来源，请在 AutomationPlatform 的 TagSources 配置中添加来源并重启后端。</div>
    </section>

    <div class="metric-grid">
      <div class="metric-card"><span class="metric-label">变量总数 <span>01 / TAGS</span></span><strong>{{ sourceState?.hasSnapshot ? tags.length : '—' }}</strong><span class="metric-caption">当前来源的变量集合</span></div>
      <div class="metric-card"><span class="metric-label">设备数量 <span>02 / DEVICES</span></span><strong>{{ sourceState?.hasSnapshot ? devices.length : '—' }}</strong><span class="metric-caption">当前来源内的设备</span></div>
      <div class="metric-card"><span class="metric-label">变量表数量 <span>03 / TABLES</span></span><strong>{{ sourceState?.hasSnapshot ? tables.length : '—' }}</strong><span class="metric-caption">当前来源内的变量表名称</span></div>
    </div>

    <section class="panel tags-panel" aria-label="PLC 变量表" :aria-busy="loading">
      <div class="panel-header">
        <div><div class="panel-kicker">TAG REGISTRY <span>—— 01</span></div><h2>变量集合</h2></div>
        <span class="panel-counter" role="status">{{ selectedSource?.name || '未选择来源' }} · 匹配 {{ filteredTags.length }} / {{ tags.length }} 条</span>
      </div>
      <div class="tag-filters">
        <label class="tag-search">关键词
          <input v-model="keyword" type="search" placeholder="搜索名称、地址、设备…" />
        </label>
        <label>设备<select v-model="device"><option :value="null">全部设备</option><option v-for="value in devices" :key="value" :value="value">{{ value || '未指定设备' }}</option></select></label>
        <label>分组<select v-model="group"><option :value="null">全部分组</option><option v-for="value in groups" :key="value" :value="value">{{ value || '未分组' }}</option></select></label>
        <label>变量表<select v-model="table"><option :value="null">全部变量表</option><option v-for="value in tables" :key="value" :value="value">{{ value || '未指定变量表' }}</option></select></label>
        <label>数据类型<select v-model="dataType"><option :value="null">全部类型</option><option v-for="value in dataTypes" :key="value" :value="value">{{ value || '未指定类型' }}</option></select></label>
        <button class="refresh-button" type="button" @click="resetFilters">重置筛选</button>
      </div>
      <div class="panel-subhead"><span>变量定义 · 只读模式</span><span>上次成功读取：{{ updatedAt }}</span></div>
      <div v-if="error" class="notice error" role="alert">{{ error }} <button type="button" @click="loadTags()">重试</button></div>
      <div v-if="error && sourceState?.hasSnapshot" class="notice warning" role="status">当前来源刷新失败，以下为上次成功读取的缓存数据，并非最新结果。</div>
      <div v-if="!selectedSource" class="state-message">请先选择可用的设备来源。</div>
      <div v-else-if="loading" class="state-message"><span class="loading-ring"></span>正在读取 {{ selectedSource.name }} 的 PLC 变量…</div>
      <div v-else-if="error && !sourceState?.hasSnapshot" class="state-message">当前来源读取失败，请重试或选择其他来源。</div>
      <div v-else-if="tags.length === 0" class="state-message empty-state"><span class="empty-glyph">▦</span><strong>暂无变量</strong><span>当前来源返回的变量集合为空。</span></div>
      <div v-else-if="filteredTags.length === 0" class="state-message empty-state"><span class="empty-glyph">◎</span><strong>没有匹配的变量</strong><span>请调整关键词或重置筛选条件。</span></div>
      <div v-else class="tag-table-scroll" tabindex="0" role="region" aria-label="变量表，可横向滚动">
        <table class="tag-table">
          <caption class="visually-hidden">PLC 变量定义，包含设备、分组、变量表、变量名称、数据类型与地址</caption>
          <thead><tr><th scope="col">序号</th><th scope="col">变量名称</th><th scope="col">数据类型</th><th scope="col">地址</th><th scope="col">变量表</th><th scope="col">分组</th><th scope="col">设备</th></tr></thead>
          <tbody>
            <tr v-for="(tag, index) in visibleTags" :key="index">
              <td class="tag-index">{{ (page - 1) * pageSize + index + 1 }}</td>
              <th scope="row">{{ tag.Name || '—' }}</th>
              <td><span class="tag-type">{{ tag.DataType || '—' }}</span></td>
              <td class="tag-address">{{ tag.Address || '—' }}</td>
              <td>{{ tag.Table || '—' }}</td><td>{{ tag.Group || '未分组' }}</td><td>{{ tag.Device || '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="pager">
        <button type="button" :disabled="page <= 1 || loading" @click="page--">← 上一页</button>
        <span>第 {{ page }} / {{ pageCount }} 页 · 每页 {{ pageSize }} 条</span>
        <button type="button" :disabled="page >= pageCount || loading" @click="page++">下一页 →</button>
      </div>
    </section>
    <footer class="page-footer"><span>CENTRALADMIN / PLC TAGS</span><span>AutomationPlatform 设备来源 API · 只读模式</span></footer>
  </div>
</template>
