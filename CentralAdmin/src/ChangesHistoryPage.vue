<script setup>
import { computed, onActivated, onDeactivated, onMounted, onUnmounted, ref, watch } from 'vue'
import { getTagSources } from './api/tags'
import { historyObjectTypes, historyStatuses } from './api/changesHistory'
import { createChangesHistoryState } from './composables/changesHistoryState'

const history = createChangesHistoryState()
const { selectedSourceId, state } = history
const sources = ref([])
const sourcesLoading = ref(false)
const sourcesError = ref('')
const sortNewestFirst = ref(false)
const selectedSource = computed(() => sources.value.find((source) => source.id === selectedSourceId.value))
const document = computed(() => state.value?.document)
const entries = computed(() => {
  const items = [...(document.value?.Entries ?? [])]
  return items.sort((left, right) => {
    const difference = Date.parse(left.LatestProject.ModifiedUtc) - Date.parse(right.LatestProject.ModifiedUtc)
    return sortNewestFirst.value ? -difference : difference
  })
})
const selectedEntry = computed(() => entries.value.find((entry) => entry.id === state.value?.selectedEntryId))
const changedCount = computed(() => entries.value.reduce((sum, entry) => sum + entry.changedCount, 0))
const filteredBlocks = computed(() => {
  const search = state.value?.keyword.trim().toLocaleLowerCase() ?? ''
  return (selectedEntry.value?.blocks ?? []).filter((block) =>
    (!state.value?.onlyChanges || block.ComparisonResult !== 'Identical') &&
    (!state.value?.objectType || block.ObjectType === state.value.objectType) &&
    (!search || [block.Device, block.LeftName, block.RightName, historyObjectTypes[block.ObjectType]]
      .some((value) => value?.toLocaleLowerCase().includes(search)))
  )
})
const pageSize = 25
const pageCount = computed(() => Math.max(1, Math.ceil(filteredBlocks.value.length / pageSize)))
const blockGroups = computed(() => {
  const groups = new Map()
  const offset = ((state.value?.page ?? 1) - 1) * pageSize
  for (const block of filteredBlocks.value.slice(offset, offset + pageSize)) {
    if (!groups.has(block.Device)) groups.set(block.Device, [])
    groups.get(block.Device).push(block)
  }
  return [...groups].map(([device, blocks]) => ({ device, blocks }))
})
const sourceStatus = computed(() => state.value?.loading ? '正在读取'
  : state.value?.error ? (document.value ? '刷新失败 · 显示旧快照' : '读取失败')
    : document.value ? '已读取' : '尚未读取')
let sourcesController
let sourcesLoaded = false

watch(() => [state.value?.keyword, state.value?.objectType, state.value?.onlyChanges, state.value?.selectedEntryId], () => {
  if (state.value) state.value.page = 1
})

function formatTime(value) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }).format(new Date(value))
}

function blockTime(value) {
  return value ? value.replace('T', ' ') : '—'
}

function projectName(path) {
  return path.split(/[\\/]/).at(-1) || path
}

function selectEntry(entry) {
  state.value.selectedEntryId = entry.id
}

function changePage(page) {
  if (state.value && page >= 1 && page <= pageCount.value) state.value.page = page
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
    if (!result.some((source) => source.id === selectedSourceId.value)) {
      history.selectSource(result[0]?.id ?? '')
    }
    history.retainSources(result.map((source) => source.id))
  } catch (error) {
    if (sourcesController === controller && error.name !== 'AbortError') {
      sourcesError.value = error.message
      sources.value = []
      history.selectSource('')
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
  history.deactivate()
}

onMounted(loadSources)
onActivated(() => {
  history.activate()
  if (!sourcesLoaded && !sourcesLoading.value && !sourcesError.value) loadSources()
})
onDeactivated(leavePage)
onUnmounted(leavePage)
</script>

<template>
  <div id="changes-history" class="dashboard changes-dashboard">
    <div class="title-row">
      <div>
        <div class="eyebrow"><span class="eyebrow-line"></span> PROGRAM CHANGE HISTORY <span class="eyebrow-index">/ 04</span></div>
        <h1>程序更改历史<span class="title-period">.</span></h1>
        <p class="subtitle">按工程时间线浏览相邻快照的程序块与工艺对象变化，复用服务端历史缓存。</p>
      </div>
      <button class="refresh-button" type="button" :disabled="state?.loading || sourcesLoading || !selectedSource" @click="history.load()">
        <span aria-hidden="true">↻</span> {{ state?.loading ? '读取中…' : '刷新当前来源' }}
      </button>
    </div>

    <section class="panel tag-source-panel" aria-label="历史记录设备来源">
      <div class="tag-source-controls">
        <label for="history-source">设备来源
          <select id="history-source" :value="selectedSourceId" :disabled="sourcesLoading || !sources.length" @change="history.selectSource($event.target.value)">
            <option v-if="!sources.length" value="">{{ sourcesLoading ? '正在加载来源…' : '暂无可用来源' }}</option>
            <option v-for="source in sources" :key="source.id" :value="source.id">{{ source.name }} · {{ source.id }}</option>
          </select>
        </label>
        <div class="tag-source-status" :class="{ 'source-error': state?.error }" role="status" aria-live="polite">
          <span>{{ selectedSource ? sourceStatus : '未选择来源' }}</span>
          <small>上次成功读取：{{ formatTime(state?.fetchedAtUtc) }}</small>
        </div>
        <button class="refresh-button" type="button" :disabled="sourcesLoading" @click="loadSources">{{ sourcesLoading ? '加载中…' : '重新加载来源' }}</button>
      </div>
      <div v-if="sourcesError" class="notice error" role="alert">{{ sourcesError }} <button type="button" @click="loadSources">重试</button></div>
      <div v-else-if="!sourcesLoading && !sources.length" class="notice warning" role="status">暂无设备来源，请在 src/config/tagSources.js 中配置服务地址。</div>
    </section>

    <div class="notice warning history-limitation" role="note">仅比较程序块与工艺对象的 ModifiedDate，不比较代码内容、具体工艺参数或变量表。刷新不强制后端重算；首次生成可能较慢，离开页面只取消浏览器等待，后端可能继续生成。</div>
    <div v-if="document?.Version === 1" class="notice warning" role="status">当前来源返回旧版历史，仅包含程序块，不包含工艺对象。升级并重启后端、重新生成历史后刷新页面。</div>
    <div v-if="state?.error" class="notice error" role="alert">
      {{ state.error }} <strong v-if="document">以下为上次成功读取的旧快照，不代表最新结果。</strong>
      <button type="button" :disabled="state.loading" @click="history.load()">重试</button>
    </div>
    <div v-if="state?.loading" class="state-message" role="status" aria-live="polite"><span class="loading-ring"></span>正在读取更改历史，首次生成最多等待 5 分钟…<span v-if="document">下方暂保留上次快照。</span></div>

    <div class="metric-grid">
      <div class="metric-card"><span class="metric-label">工程快照 <span>01 / PROJECTS</span></span><strong>{{ document?.Projects.length ?? '—' }}</strong><span class="metric-caption">当前来源的历史工程</span></div>
      <div class="metric-card"><span class="metric-label">相邻比较 <span>02 / COMPARISONS</span></span><strong>{{ document ? entries.length : '—' }}</strong><span class="metric-caption">按旧工程 → 新工程排列</span></div>
      <div class="metric-card"><span class="metric-label">变化对象记录 <span>03 / CHANGES</span></span><strong class="metric-amber">{{ document ? changedCount : '—' }}</strong><span class="metric-caption">跨比较累计，含新增 / 删除 / 修改，非去重</span></div>
    </div>

    <div v-if="document" class="workspace-grid history-workspace" :aria-busy="state?.loading">
      <section class="panel" aria-label="相邻工程比较列表">
        <div class="panel-header"><div><div class="panel-kicker">PROJECT TIMELINE <span>—— 01</span></div><h2>工程时间线</h2></div><span class="panel-counter">{{ entries.length }} 条比较</span></div>
        <div class="panel-subhead history-subhead"><button class="history-sort-button" type="button" :aria-pressed="sortNewestFirst" :aria-label="`当前按修改时间${sortNewestFirst ? '从新到旧' : '从旧到新'}排序，点击切换`" @click="sortNewestFirst = !sortNewestFirst">{{ sortNewestFirst ? '新 → 旧' : '旧 → 新' }} · 按修改时间排序</button><span :title="document.GeneratedAtUtc">生成：{{ formatTime(document.GeneratedAtUtc) }}</span></div>
        <div v-if="!entries.length" class="state-message empty-state"><span class="empty-glyph">◎</span><strong>暂无历史比较记录</strong><span>服务返回的历史列表为空。</span></div>
        <div v-else class="scan-list history-entry-list">
          <button v-for="(entry, index) in entries" :key="entry.id" class="scan-item history-entry" :class="{ selected: entry.id === state.selectedEntryId }" type="button" :aria-pressed="entry.id === state.selectedEntryId" @click="selectEntry(entry)">
            <span class="scan-indicator"></span>
            <span class="scan-info">
              <span class="scan-topline"><strong>比较 {{ String(index + 1).padStart(2, '0') }}</strong><small>{{ entry.changedCount ? `${entry.changedCount} 个变化对象` : '无变化' }}</small></span>
              <span class="history-project-name" :title="entry.PreviousProject.Path">{{ projectName(entry.PreviousProject.Path) }}</span>
              <span class="history-direction" aria-hidden="true">↓</span>
              <span class="history-project-name" :title="entry.LatestProject.Path">{{ projectName(entry.LatestProject.Path) }}</span>
              <span class="scan-date">新工程修改：{{ formatTime(entry.LatestProject.ModifiedUtc) }}</span>
              <span class="scan-interface">比较执行：{{ formatTime(entry.ComparedAtUtc) }}</span>
            </span>
            <span class="scan-arrow" aria-hidden="true">›</span>
          </button>
        </div>
      </section>

      <section class="panel history-detail" aria-label="程序块与工艺对象变更详情">
        <div class="panel-header"><div><div class="panel-kicker">OBJECT DIFFERENCES <span>—— 02</span></div><h2>对象详情</h2></div><span v-if="selectedEntry" class="panel-counter">{{ selectedEntry.Comparisons.length }} 台 PLC · {{ selectedEntry.blocks.length }} 个对象</span></div>
        <template v-if="selectedEntry">
          <div class="history-projects">
            <div><span>旧工程</span><strong>{{ selectedEntry.PreviousProject.Path }}</strong><small>文件修改：{{ formatTime(selectedEntry.PreviousProject.ModifiedUtc) }}</small></div>
            <div><span>新工程</span><strong>{{ selectedEntry.LatestProject.Path }}</strong><small>文件修改：{{ formatTime(selectedEntry.LatestProject.ModifiedUtc) }}</small></div>
          </div>
          <div class="panel-subhead history-subhead"><span>比较执行：{{ formatTime(selectedEntry.ComparedAtUtc) }}</span><span>对象修改时间保留接口原值；Z 表示 UTC</span></div>
          <div class="history-status-summary" aria-label="对象状态统计">
            <span v-for="(label, status) in historyStatuses" :key="status" class="history-badge" :class="status">{{ label }} {{ selectedEntry.counts[status] }}</span>
          </div>
          <div class="tag-filters history-filters">
            <label class="tag-search" for="history-search">搜索 PLC / 对象 / 分组<input id="history-search" v-model="state.keyword" type="search" placeholder="输入名称或分组路径" /></label>
            <label for="history-object-type">对象类型<select id="history-object-type" v-model="state.objectType"><option value="">全部类型</option><option v-for="(label, type) in historyObjectTypes" :key="type" :value="type">{{ label }}</option></select></label>
            <label class="history-only-changes" for="history-only-changes"><input id="history-only-changes" v-model="state.onlyChanges" type="checkbox" />只看变化</label>
          </div>
          <div v-if="!filteredBlocks.length" class="state-message empty-state">
            <span class="empty-glyph">◇</span>
            <strong>{{ !selectedEntry.blocks.length ? '该次比较没有对象' : !selectedEntry.changedCount && state.onlyChanges && !state.keyword.trim() && !state.objectType ? '所有对象的 ModifiedDate 均相同' : '暂无匹配的对象' }}</strong>
            <span v-if="selectedEntry.blocks.length">可调整类型、搜索条件或取消“只看变化”查看全部对象。</span>
          </div>
          <section v-for="group in blockGroups" :key="group.device" class="history-plc" :aria-label="`${group.device} 比较对象`">
            <div class="device-heading"><h3>PLC · {{ group.device }}</h3><span>本页 {{ group.blocks.length }} 个对象</span></div>
            <div class="tag-table-scroll history-table-scroll" tabindex="0" :aria-label="`${group.device} 对象比较表，可横向滚动`">
              <table class="tag-table history-table">
                <caption class="visually-hidden">{{ group.device }} 程序块与工艺对象修改时间比较</caption>
                <thead><tr><th scope="col">对象 / 分组</th><th scope="col">类型</th><th scope="col">状态</th><th scope="col">旧 ModifiedDate</th><th scope="col">新 ModifiedDate</th></tr></thead>
                <tbody><tr v-for="block in group.blocks" :key="JSON.stringify([block.ObjectType, block.LeftName, block.RightName])">
                  <th scope="row">{{ block.LeftName || block.RightName }}</th>
                  <td>{{ historyObjectTypes[block.ObjectType] }}</td>
                  <td><span class="history-badge" :class="block.ComparisonResult">{{ historyStatuses[block.ComparisonResult] }}</span></td>
                  <td class="mono history-block-time">{{ blockTime(block.PreviousModifiedDate) }}</td>
                  <td class="mono history-block-time">{{ blockTime(block.LatestModifiedDate) }}</td>
                </tr></tbody>
              </table>
            </div>
          </section>
          <div class="pager"><button type="button" :disabled="state.page <= 1" @click="changePage(state.page - 1)">← 上一页</button><span>{{ state.page }} / {{ pageCount }} · 共 {{ filteredBlocks.length }} 个对象</span><button type="button" :disabled="state.page >= pageCount" @click="changePage(state.page + 1)">下一页 →</button></div>
        </template>
        <div v-else class="state-message empty-state"><span class="empty-glyph">▤</span><strong>请选择历史比较记录</strong><span>从左侧时间线选择记录查看对象详情。</span></div>
      </section>
    </div>
    <div v-else-if="!state?.loading && !state?.error && !sourcesLoading && selectedSource" class="state-message empty-state"><strong>尚未读取更改历史</strong><span>点击“刷新当前来源”读取历史。</span></div>
    <footer class="page-footer"><span>CENTRALADMIN / CHANGE HISTORY</span><span>数据由 WindowsServiceV14 提供 · ModifiedDate 比较</span></footer>
  </div>
</template>

<style scoped>
.history-limitation{margin-bottom:24px;line-height:1.8}
.history-workspace{grid-template-columns:minmax(260px,.65fr) minmax(0,1.6fr)}
.history-subhead{flex-wrap:wrap;line-height:1.7}
.history-sort-button{padding:0;border:0;background:transparent;color:inherit;font:inherit;text-align:left;cursor:pointer}
.history-sort-button:hover{color:#dcece8}
.history-sort-button:focus-visible{outline:2px solid #58dbb8;outline-offset:3px}
.history-entry{align-items:flex-start}
.history-entry .scan-indicator{margin-top:4px}
.history-entry .scan-arrow{align-self:center}
.history-project-name{font-size:12px;color:#d6e9e6;overflow-wrap:anywhere;line-height:1.6}
.history-direction{color:#58dbb8;font-family:'DM Mono',monospace}
.history-projects{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:20px;padding:22px 24px;border-bottom:1px solid #253842}
.history-projects>div{min-width:0;display:flex;flex-direction:column;gap:8px}
.history-projects span{font-size:10px;color:#75919c}
.history-projects strong{font-size:12px;font-weight:600;color:#dcece8;overflow-wrap:anywhere;line-height:1.7}
.history-projects small{font-size:10px;color:#91aab3;line-height:1.7}
.history-status-summary{display:flex;flex-wrap:wrap;gap:10px;padding:18px 24px 0}
.history-badge{display:inline-block;white-space:nowrap;padding:5px 8px;border:1px solid #35545a;border-radius:3px;font-size:10px;background:#172b34;color:#a0bbc1}
.history-badge.Different{background:#362b19;border-color:#796035;color:#f0cb8b}
.history-badge.Added{background:#163a30;border-color:#346853;color:#8be4bc}
.history-badge.Removed{background:#3a2429;border-color:#78434c;color:#f0a6ad}
.history-filters .history-only-changes{flex:0 0 auto;flex-direction:row;align-items:center;gap:9px;align-self:flex-end;min-height:40px}
.history-filters .history-only-changes input{width:16px;height:16px;padding:0;accent-color:#55d4b6}
.history-plc .device-heading{margin:0;padding:18px 24px;background:#13252e}
.history-plc .device-heading h3{overflow-wrap:anywhere}
.history-plc .device-heading span{white-space:nowrap}
.history-table{min-width:700px}
.history-table .history-block-time{font-size:10px;min-width:190px;line-height:1.8}
.history-table-scroll{max-height:none}
.history-detail .pager{gap:12px}
.history-detail .pager>span{text-align:center;font-size:10px}
@media(max-width:1100px){.history-workspace{grid-template-columns:1fr}.history-entry-list{max-height:360px}}
@media(max-width:600px){.history-projects{grid-template-columns:1fr;padding:18px 20px}.history-status-summary{padding:18px 20px 0}.history-detail .panel-header{flex-wrap:wrap}.history-plc .device-heading{padding:16px 20px;gap:10px;flex-wrap:wrap}}
</style>
