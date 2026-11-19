<script setup>
import { computed, onActivated, onDeactivated, onMounted, onUnmounted, ref } from 'vue'
import { getTagSources } from './api/tags'
import { validateMaximumEntries } from './api/serviceEventLogs'
import { createServiceEventLogsState, logLevelKind } from './composables/serviceEventLogsState'

const logs = createServiceEventLogsState()
const { selectedSourceId, state, filteredItems, pageItems, pageCount } = logs
const sources = ref([])
const sourcesLoading = ref(false)
const sourcesError = ref('')
const selectedSource = computed(() => sources.value.find((source) => source.id === selectedSourceId.value))
const sourceStatus = computed(() => state.value?.loading ? '正在读取'
  : state.value?.error ? (state.value.items !== null ? '刷新失败 · 显示旧快照' : '读取失败')
    : state.value?.items !== null && state.value?.items !== undefined ? '已读取' : '尚未读取')
let sourcesController
let sourcesLoaded = false

function formatTime(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN', { hour12: false })
}

async function loadSources() {
  if (sourcesLoading.value) return
  const controller = new AbortController()
  sourcesController = controller
  sourcesLoading.value = true
  sourcesError.value = ''
  try {
    const result = await getTagSources(controller.signal)
    if (sourcesController !== controller || controller.signal.aborted) return
    sources.value = result
    sourcesLoaded = true
    logs.retainSources(result.map((source) => source.id))
    if (!result.some((source) => source.id === selectedSourceId.value)) logs.selectSource(result[0]?.id ?? '')
  } catch (error) {
    if (sourcesController === controller && error.name !== 'AbortError') {
      sourcesError.value = error.message
      sources.value = []
      logs.selectSource('')
    }
  } finally {
    if (sourcesController === controller) {
      sourcesLoading.value = false
      sourcesController = undefined
    }
  }
}

function leavePage() {
  sourcesController?.abort()
  sourcesController = undefined
  sourcesLoading.value = false
  logs.deactivate()
}

onMounted(loadSources)
onActivated(() => {
  logs.activate()
  if (!sourcesLoaded && !sourcesLoading.value && !sourcesError.value) loadSources()
})
onDeactivated(leavePage)
onUnmounted(leavePage)
</script>

<template>
  <div id="service-event-logs" class="dashboard service-logs-dashboard">
    <div class="title-row">
      <div>
        <div class="eyebrow"><span class="eyebrow-line"></span> SERVICEV14 EVENT LOG <span class="eyebrow-index">/ 05</span></div>
        <h1>ServiceV14 日志<span class="title-period">.</span></h1>
        <p class="subtitle">读取所选来源机器的 TIA Portal 操作日志，最新优先；不会打开工程。</p>
      </div>
      <button class="refresh-button" type="button" :disabled="state?.loading || sourcesLoading || !selectedSource || !validateMaximumEntries(state?.maximumEntries)" @click="logs.load()">
        {{ state?.loading ? '读取中…' : '刷新当前来源' }}
      </button>
    </div>

    <section class="panel tag-source-panel" aria-label="日志设备来源">
      <div class="tag-source-controls">
        <label for="service-log-source">设备来源
          <select id="service-log-source" :value="selectedSourceId" :disabled="sourcesLoading || !sources.length" @change="logs.selectSource($event.target.value)">
            <option v-if="!sources.length" value="">{{ sourcesLoading ? '正在加载来源…' : '暂无可用来源' }}</option>
            <option v-for="source in sources" :key="source.id" :value="source.id">{{ source.name }} · {{ source.id }}</option>
          </select>
        </label>
        <label for="service-log-limit">读取条数（1–5000）
          <input id="service-log-limit" type="number" min="1" max="5000" step="1" :disabled="!state" :value="state?.maximumEntries ?? 100"
            :aria-invalid="state && !validateMaximumEntries(state.maximumEntries) ? 'true' : 'false'"
            @input="logs.setMaximumEntries($event.target.value === '' ? null : Number($event.target.value))" />
        </label>
        <div class="tag-source-status" :class="{ 'source-error': state?.error }" role="status" aria-live="polite">
          <span>{{ selectedSource ? sourceStatus : '未选择来源' }}</span>
          <small>上次成功读取：{{ formatTime(state?.fetchedAtUtc) }}</small>
        </div>
        <button class="refresh-button" type="button" :disabled="sourcesLoading" @click="loadSources">重新加载来源</button>
      </div>
      <div v-if="sourcesError" class="notice error" role="alert">{{ sourcesError }}</div>
      <div v-else-if="!sourcesLoading && !sources.length" class="notice warning" role="status">暂无设备来源，请在 src/config/tagSources.js 中配置 ServiceV14 地址。</div>
    </section>

    <p class="notice warning" role="note">通道：SiemensAG-Automation-TIAPortal/Operational。仅手动刷新，不自动轮询；切换来源不会混合不同机器的日志。</p>
    <div v-if="state && !validateMaximumEntries(state.maximumEntries)" class="notice error" role="alert">读取条数必须为 1 到 5000 的整数。</div>
    <div v-if="state?.error" class="notice error" role="alert">
      {{ state.error }} <strong v-if="state.items !== null">以下为上次成功读取的旧快照，不代表最新结果。</strong>
      <button type="button" :disabled="state.loading || !validateMaximumEntries(state.maximumEntries)" @click="logs.load()">重试</button>
    </div>
    <div v-if="state?.loading" class="state-message" role="status"><span class="loading-ring"></span>正在读取 ServiceV14 日志…<span v-if="state.items !== null">下方暂保留上次快照。</span></div>

    <section v-if="state" class="panel service-log-panel" aria-label="ServiceV14 日志列表" :aria-busy="state.loading">
      <div class="panel-header">
        <div><div class="panel-kicker">EVENT LOG</div><h2>{{ selectedSource?.name }} · 操作日志</h2></div>
        <span class="panel-counter">{{ state.items?.length ?? '—' }} 条记录</span>
      </div>
      <div class="service-log-filters">
        <label for="service-log-keyword">关键词
          <input id="service-log-keyword" type="search" placeholder="搜索用户、内容、计算机、事件 ID…" :value="state.keyword" @input="logs.setFilter('keyword', $event.target.value)" />
        </label>
        <label for="service-log-level">日志级别
          <select id="service-log-level" :value="state.level" @change="logs.setFilter('level', $event.target.value)">
            <option value="all">全部级别</option><option value="error">错误 / 严重</option><option value="warning">警告</option><option value="info">信息 / 其他</option>
          </select>
        </label>
      </div>
      <div v-if="state.items !== null" class="panel-subhead">
        <span>匹配 {{ filteredItems.length }} / {{ state.items.length }} 条 · 快照读取上限 {{ state.fetchedMaximumEntries }}</span>
        <span v-if="state.maximumEntries !== state.fetchedMaximumEntries">条数设置已变更，请刷新应用</span>
      </div>
      <div v-if="state.items === null && !state.loading && !state.error" class="state-message" role="status">尚未读取，请刷新当前来源。</div>
      <div v-else-if="state.items !== null && !filteredItems.length" class="state-message empty-state" role="status">
        <strong>{{ state.items.length ? '没有匹配的日志' : '暂无日志记录' }}</strong>
        <span>{{ state.items.length ? '请调整关键词或级别筛选。' : '所选来源日志通道当前没有记录。' }}</span>
      </div>
      <div v-else-if="pageItems.length" class="service-log-table-scroll" tabindex="0" aria-label="可横向滚动的日志表格">
        <table class="service-log-table">
          <thead><tr><th scope="col">时间</th><th scope="col">用户</th><th scope="col">计算机</th><th scope="col">事件内容</th><th scope="col">事件 ID</th><th scope="col">级别</th><th scope="col">提供程序</th><th scope="col">记录 ID</th></tr></thead>
          <tbody>
            <tr v-for="(entry, index) in pageItems" :key="index">
              <td :title="entry.timeCreated || ''">{{ formatTime(entry.timeCreated) }}</td><td>{{ entry.user || '—' }}</td><td>{{ entry.computer || '—' }}</td>
              <td class="service-log-content">{{ entry.eventContent || '—' }}</td><td>{{ entry.eventId }}</td>
              <td><span class="service-log-level" :class="logLevelKind(entry.level)">{{ entry.level || '—' }}</span></td><td>{{ entry.provider || '—' }}</td><td>{{ entry.recordId || '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-if="state.items !== null" class="pager" aria-label="日志分页">
        <button type="button" :disabled="state.page <= 1" @click="logs.changePage(state.page - 1)">上一页</button>
        <span>{{ state.page }} / {{ pageCount }} <small>· 每页 25 条</small></span>
        <button type="button" :disabled="state.page >= pageCount" @click="logs.changePage(state.page + 1)">下一页</button>
      </div>
    </section>
  </div>
</template>

<style scoped>
.service-logs-dashboard > .notice { margin-bottom: 20px; overflow-wrap: anywhere; }
.service-logs-dashboard input, .service-logs-dashboard select { width: 100%; min-width: 0; border: 1px solid #34515b; border-radius: 4px; background: #0c1922; color: #e2efee; padding: 10px 12px; font: inherit; }
.service-logs-dashboard input:focus-visible, .service-logs-dashboard select:focus-visible, .service-log-table-scroll:focus-visible { outline: 2px solid #42dbc4; outline-offset: 2px; }
.service-logs-dashboard label { display: flex; flex-direction: column; gap: 8px; min-width: 0; color: #9ab5bd; font-size: 12px; }
.service-logs-dashboard .tag-source-controls { flex-wrap: wrap; }
.service-log-filters { display: flex; flex-wrap: wrap; gap: 16px; padding: 20px 24px; }
.service-log-filters label:first-child { flex: 1 1 240px; }
.service-log-filters label:last-child { flex: 0 1 180px; }
.service-log-table-scroll { width: 100%; overflow-x: auto; }
.service-log-table { border-collapse: collapse; width: 100%; min-width: 1100px; table-layout: fixed; font-size: 12px; }
.service-log-table th, .service-log-table td { border-bottom: 1px solid #253842; padding: 14px 12px; text-align: left; vertical-align: top; overflow-wrap: anywhere; }
.service-log-table th { color: #9ab5bd; font-weight: 600; }
.service-log-table th:first-child { width: 155px; }
.service-log-table th:nth-child(4) { width: 30%; }
.service-log-content { white-space: pre-wrap; }
.service-log-level.error { color: #ff9a94; }
.service-log-level.warning { color: #eabf78; }
.service-log-level.info { color: #8edbc4; }
.service-logs-dashboard .panel-subhead { flex-wrap: wrap; }
@media (max-width: 600px) {
  .service-logs-dashboard .tag-source-controls > * { width: 100%; flex: 1 1 100%; }
  .service-log-filters { padding: 16px; }
  .service-log-filters label:last-child { flex: 1 1 100%; }
  .service-log-panel .panel-header { flex-wrap: wrap; padding: 16px; }
}
</style>
