<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { getScan, getScans } from './api/discovery'
import { getOperationLogs } from './api/eventLogs'

const currentTab = ref('scans')
const selectedLogLevel = ref('all')

function syncTabFromHash() {
  const hash = window.location.hash.toLowerCase()
  if (hash === '#operation-logs' || hash === '#logs') {
    currentTab.value = 'logs'
  } else {
    currentTab.value = 'scans'
  }
}

function switchTab(tab) {
  currentTab.value = tab
  const targetHash = tab === 'logs' ? '#operation-logs' : '#scan-history'
  if (window.location.hash !== targetHash) {
    window.location.hash = targetHash
  }
}

const pageSize = 10
const page = ref(1)
const totalCount = ref(0)
const scans = ref([])
const selectedId = ref(null)
const detail = ref(null)
const listLoading = ref(false)
const detailLoading = ref(false)
const listError = ref('')
const detailError = ref('')
const operationLogs = ref([])
const logsLoading = ref(false)
const logsError = ref('')
let listController
let detailController
let logsController

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const selectedScan = computed(() => scans.value.find((scan) => scan.id === selectedId.value))
const protectedCount = computed(() => detail.value?.devices.filter((device) => device.isProtected === true).length ?? 0)
const issueCount = computed(() =>
  operationLogs.value.filter((log) => /error|错误|critical|严重|warn|警告/i.test(log.level || '')).length
)
const filteredLogs = computed(() => {
  if (selectedLogLevel.value === 'all') return operationLogs.value
  return operationLogs.value.filter((log) => {
    const lvl = log.level || ''
    if (selectedLogLevel.value === 'issue') return /error|错误|critical|严重|warn|警告/i.test(lvl)
    if (selectedLogLevel.value === 'error') return /error|错误|critical|严重/i.test(lvl)
    if (selectedLogLevel.value === 'warning') return /warn|警告/i.test(lvl)
    if (selectedLogLevel.value === 'info') return !/error|错误|critical|严重|warn|警告/i.test(lvl)
    return true
  })
})

function formatTime(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }).format(date)
}

function display(value) {
  return value || '—'
}

function levelClass(level) {
  if (/error|错误|critical|严重/i.test(level)) return 'error'
  if (/warn|警告/i.test(level)) return 'warning'
  return 'normal'
}

async function loadOperationLogs() {
  logsController?.abort()
  const controller = new AbortController()
  logsController = controller
  logsLoading.value = true
  logsError.value = ''

  try {
    operationLogs.value = await getOperationLogs(controller.signal)
  } catch (error) {
    if (error.name !== 'AbortError') {
      logsError.value = error.message
      operationLogs.value = []
    }
  } finally {
    if (logsController === controller) logsLoading.value = false
  }
}

async function loadDetail(id) {
  detailController?.abort()
  const controller = new AbortController()
  detailController = controller
  selectedId.value = id
  detail.value = null
  detailError.value = ''
  detailLoading.value = true

  try {
    detail.value = await getScan(id, controller.signal)
  } catch (error) {
    if (error.name !== 'AbortError') detailError.value = error.message
  } finally {
    if (detailController === controller) detailLoading.value = false
  }
}

async function loadPage(keepSelection = false) {
  listController?.abort()
  detailController?.abort()
  detailController = undefined
  detailLoading.value = false
  const controller = new AbortController()
  listController = controller
  listLoading.value = true
  listError.value = ''
  detailError.value = ''
  detail.value = null
  const previousId = keepSelection ? selectedId.value : null
  selectedId.value = null

  try {
    const result = await getScans(page.value, pageSize, controller.signal)
    scans.value = result.items
    totalCount.value = result.totalCount
    if (result.items.length > 0) {
      const nextId = result.items.find((scan) => scan.id === previousId)?.id ?? result.items[0].id
      await loadDetail(nextId)
    }
  } catch (error) {
    if (error.name !== 'AbortError') {
      listError.value = error.message
      scans.value = []
      totalCount.value = 0
    }
  } finally {
    if (listController === controller) listLoading.value = false
  }
}

function changePage(nextPage) {
  if (nextPage < 1 || nextPage > pageCount.value || listLoading.value) return
  page.value = nextPage
  loadPage()
}

onMounted(() => {
  syncTabFromHash()
  window.addEventListener('hashchange', syncTabFromHash)
  loadPage()
  loadOperationLogs()
})
onUnmounted(() => {
  window.removeEventListener('hashchange', syncTabFromHash)
  listController?.abort()
  detailController?.abort()
  logsController?.abort()
})
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <span class="brand-mark" aria-hidden="true">C<span>·</span>A</span>
        <div><strong>CentralAdmin</strong><small>INDUSTRIAL CONTROL</small></div>
      </div>
      <div class="sidebar-section">工作空间 <span>01</span></div>
      <a
        class="nav-item"
        :class="{ active: currentTab === 'scans' }"
        href="#scan-history"
        @click.prevent="switchTab('scans')"
      >
        <span class="nav-symbol">▦</span> 扫描历史
        <span v-if="currentTab === 'scans'" class="nav-active-dot"></span>
      </a>
      <a
        class="nav-item"
        :class="{ active: currentTab === 'logs' }"
        href="#operation-logs"
        @click.prevent="switchTab('logs')"
      >
        <span class="nav-symbol">≡</span> 操作日志
        <span v-if="currentTab === 'logs'" class="nav-active-dot"></span>
      </a>
      <div class="sidebar-note">
        <span class="signal-dot"></span>
        <span>数据源<br /><strong>AutomationPlatform API</strong></span>
      </div>
      <div class="sidebar-footer">CENTRALADMIN <span>·</span> READ ONLY</div>
    </aside>

    <main class="main-content">
      <header class="topbar">
        <div v-if="currentTab === 'scans'" class="breadcrumb">
          工作空间 <span>/</span> 设备监控 <span>/</span> <strong>扫描历史</strong>
        </div>
        <div v-else class="breadcrumb">
          工作空间 <span>/</span> 系统审计 <span>/</span> <strong>操作日志</strong>
        </div>
        <div class="topbar-right">
          <span class="topbar-live">
            <i></i> {{ currentTab === 'scans' ? '历史数据' : '事件通道' }}
          </span>
          <span class="topbar-divider"></span>
          <span>只读视图</span>
        </div>
      </header>

      <!-- 扫描历史页面 -->
      <div v-if="currentTab === 'scans'" id="scan-history" class="dashboard">
        <div class="title-row">
          <div>
            <div class="eyebrow"><span class="eyebrow-line"></span> DISCOVERY OVERVIEW <span class="eyebrow-index">/ 01</span></div>
            <h1>设备扫描历史<span class="title-period">.</span></h1>
            <p class="subtitle">查看历史扫描批次与设备快照，数据来自 AutomationPlatform。</p>
          </div>
          <button class="refresh-button" type="button" :disabled="listLoading" @click="loadPage(true)">
            <span aria-hidden="true">↻</span> {{ listLoading ? '刷新中…' : '刷新数据' }}
          </button>
        </div>

        <div class="metric-grid">
          <div class="metric-card"><span class="metric-label">历史扫描总数 <span>01 / TOTAL</span></span><strong>{{ totalCount }}</strong><span class="metric-caption">当前数据库记录</span></div>
          <div class="metric-card"><span class="metric-label">当前批次设备 <span>02 / DEVICES</span></span><strong>{{ detail?.devices.length ?? '—' }}</strong><span class="metric-caption">所选扫描的设备快照</span></div>
          <div class="metric-card"><span class="metric-label">受保护设备 <span>03 / PROTECTED</span></span><strong class="metric-amber">{{ detail ? protectedCount : '—' }}</strong><span class="metric-caption">当前批次 CPU 保护状态</span></div>
        </div>

        <div class="workspace-grid">
          <section class="panel history-panel" aria-label="扫描批次列表">
            <div class="panel-header">
              <div><div class="panel-kicker">SCAN LOG <span>—— 01</span></div><h2>扫描批次</h2></div>
              <span class="panel-counter">{{ totalCount }} 条记录</span>
            </div>
            <div class="panel-subhead"><span>按扫描时间排序 · 最新优先</span><span>第 {{ page }} / {{ pageCount }} 页</span></div>
            <div v-if="listError" class="notice error" role="alert">{{ listError }} <button type="button" @click="loadPage()">重试</button></div>
            <div v-else-if="listLoading" class="state-message"><span class="loading-ring"></span>正在加载扫描历史…</div>
            <div v-else-if="scans.length === 0" class="state-message empty-state"><span class="empty-glyph">◎</span><strong>暂无扫描记录</strong><span>请先在 AutomationPlatform 执行一次设备扫描。</span></div>
            <div v-else class="scan-list">
              <button v-for="scan in scans" :key="scan.id" class="scan-item" :class="{ selected: scan.id === selectedId }" type="button" :aria-pressed="scan.id === selectedId" @click="loadDetail(scan.id)">
                <span class="scan-indicator"></span>
                <span class="scan-info"><span class="scan-topline"><strong>扫描 #{{ scan.id }}</strong><small>{{ scan.deviceCount }} 台设备</small></span><span class="scan-date">{{ formatTime(scan.scannedAtUtc) }}</span><span class="scan-interface">网卡 · {{ scan.networkInterface }}</span></span>
                <span class="scan-arrow" aria-hidden="true">›</span>
              </button>
            </div>
            <div class="pager"><button type="button" :disabled="page <= 1 || listLoading" @click="changePage(page - 1)">← 上一页</button><span>{{ page }} <small>/ {{ pageCount }}</small></span><button type="button" :disabled="page >= pageCount || listLoading" @click="changePage(page + 1)">下一页 →</button></div>
          </section>

          <section class="panel detail-panel" aria-label="扫描设备详情">
            <div class="panel-header"><div><div class="panel-kicker">DEVICE INVENTORY <span>—— 02</span></div><h2>设备快照</h2></div><span class="panel-counter">{{ selectedId ? `#${selectedId}` : '未选择' }}</span></div>
            <div v-if="detailError" class="notice error" role="alert">{{ detailError }} <button v-if="selectedId" type="button" @click="loadDetail(selectedId)">重试</button></div>
            <div v-else-if="detailLoading" class="state-message"><span class="loading-ring"></span>正在读取设备详情…</div>
            <template v-else-if="detail">
              <div class="detail-meta">
                <div><span>扫描时间</span><strong>{{ formatTime(detail.scannedAtUtc) }}</strong></div>
                <div><span>网络接口</span><strong :title="detail.networkInterface">{{ detail.networkInterface }}</strong></div>
                <div><span>过滤条件</span><strong :title="detail.filter">{{ display(detail.filter) }}</strong></div>
              </div>
              <div v-if="detail.warning" class="notice warning" role="status">扫描提示：{{ detail.warning }}</div>
              <div class="device-heading"><h3>发现的设备</h3><span>{{ detail.devices.length }} DEVICES</span></div>
              <div v-if="detail.devices.length === 0" class="state-message empty-state"><span class="empty-glyph">◇</span><strong>本次扫描未发现设备</strong><span>扫描记录存在，但没有关联设备。</span></div>
              <div v-else class="device-list">
                <article v-for="(device, index) in detail.devices" :key="index" class="device-card">
                  <div class="device-card-head"><span class="device-icon">▣</span><div class="device-identity"><strong>{{ display(device.profinetName) }}</strong><span>{{ display(device.description) }}</span></div><span class="device-badge" :class="device.isProtected === true ? 'protected' : 'normal'">{{ device.isProtected === true ? '受保护' : '设备' }}</span></div>
                  <div class="device-data"><div><span>IP 地址</span><strong class="mono accent-text">{{ display(device.ipAddress) }}</strong></div><div><span>MAC 地址</span><strong class="mono">{{ display(device.macAddress) }}</strong></div><div><span>订货号</span><strong class="mono">{{ display(device.articleNumber) }}</strong></div><div><span>运行状态</span><strong>{{ display(device.operatingMode) }}</strong></div></div>
                  <div v-if="device.statusError" class="device-warning">状态读取失败：{{ device.statusError }}</div>
                </article>
              </div>
            </template>
            <div v-else class="state-message empty-state"><span class="empty-glyph">▦</span><strong>请选择扫描批次</strong><span>从左侧列表选择一条记录查看设备详情。</span></div>
          </section>
        </div>
        <footer class="page-footer"><span>CENTRALADMIN / SCAN HISTORY</span><span>数据由 AutomationPlatform 提供 · 只读模式</span></footer>
      </div>

      <!-- 操作日志页面 -->
      <div v-else-if="currentTab === 'logs'" id="operation-logs" class="dashboard">
        <div class="title-row">
          <div>
            <div class="eyebrow"><span class="eyebrow-line"></span> AUDIT & MONITORING <span class="eyebrow-index">/ 02</span></div>
            <h1>TIA Portal 操作日志<span class="title-period">.</span></h1>
            <p class="subtitle">读取 Windows 事件日志通道 (SiemensAG-Automation-TIAPortal/Operational)，监控自动化平台操作记录。</p>
          </div>
          <button class="refresh-button" type="button" :disabled="logsLoading" @click="loadOperationLogs">
            <span aria-hidden="true">↻</span> {{ logsLoading ? '刷新中…' : '刷新日志' }}
          </button>
        </div>

        <div class="metric-grid">
          <div class="metric-card">
            <span class="metric-label">事件总数 <span>01 / TOTAL</span></span>
            <strong>{{ operationLogs.length }}</strong>
            <span class="metric-caption">当前已获取最新事件</span>
          </div>
          <div class="metric-card">
            <span class="metric-label">异常与警告 <span>02 / ISSUES</span></span>
            <strong :class="{ 'metric-amber': issueCount > 0 }">{{ issueCount }}</strong>
            <span class="metric-caption">警告与错误级别记录</span>
          </div>
          <div class="metric-card">
            <span class="metric-label">事件通道 <span>03 / CHANNEL</span></span>
            <strong class="metric-text">Operational</strong>
            <span class="metric-caption">SiemensAG-Automation-TIAPortal</span>
          </div>
        </div>

        <section class="panel operation-panel full-page" aria-label="TIA Portal 操作日志">
          <div class="panel-header">
            <div><div class="panel-kicker">TIA PORTAL <span>—— 01</span></div><h2>操作日志</h2></div>
            <div class="panel-actions">
              <div class="log-filter-group" role="group" aria-label="日志级别筛选">
                <button
                  class="log-filter-btn"
                  :class="{ active: selectedLogLevel === 'all' }"
                  type="button"
                  @click="selectedLogLevel = 'all'"
                >
                  全部 ({{ operationLogs.length }})
                </button>
                <button
                  class="log-filter-btn"
                  :class="{ active: selectedLogLevel === 'issue' }"
                  type="button"
                  @click="selectedLogLevel = 'issue'"
                >
                  异常/警告 ({{ issueCount }})
                </button>
                <button
                  class="log-filter-btn"
                  :class="{ active: selectedLogLevel === 'info' }"
                  type="button"
                  @click="selectedLogLevel = 'info'"
                >
                  信息
                </button>
              </div>
              <span class="panel-counter">显示 {{ filteredLogs.length }} / {{ operationLogs.length }} 条</span>
            </div>
          </div>
          <div class="panel-subhead">
            <span>SiemensAG-Automation-TIAPortal/Operational</span>
            <span>按生成时间倒序排列</span>
          </div>
          <div v-if="logsError" class="notice error" role="alert">{{ logsError }} <button type="button" @click="loadOperationLogs">重试</button></div>
          <div v-if="logsLoading" class="state-message"><span class="loading-ring"></span>正在读取操作日志…</div>
          <div v-else-if="!logsError && filteredLogs.length === 0" class="state-message empty-state"><span class="empty-glyph">≡</span><strong>暂无匹配操作日志</strong><span>当前筛选条件下没有日志记录。</span></div>
          <div v-else-if="!logsError" class="operation-list">
            <article v-for="(log, index) in filteredLogs" :key="log.recordId || index" class="operation-item">
              <div class="operation-item-header">
                <span class="operation-time">{{ formatTime(log.timeCreated) }}</span>
                <span class="log-level" :class="levelClass(log.level)">{{ display(log.level) }}</span>
                <span class="operation-id">EVENT #{{ log.eventId }}</span>
              </div>
              <p class="operation-content">{{ display(log.eventContent) }}</p>
              <div class="operation-meta">
                <span>用户 <strong>{{ display(log.user) }}</strong></span>
                <span>计算机 <strong>{{ display(log.computer) }}</strong></span>
                <span>来源 <strong>{{ display(log.provider) }}</strong></span>
                <span>记录 ID <strong>{{ display(log.recordId) }}</strong></span>
              </div>
            </article>
          </div>
        </section>
        <footer class="page-footer"><span>CENTRALADMIN / OPERATION LOGS</span><span>数据由 AutomationPlatform 提供 · 只读模式</span></footer>
      </div>
    </main>
  </div>
</template>
