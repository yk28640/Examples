import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createServer } from 'vite'

const root = fileURLToPath(new URL('../', import.meta.url))
const browserPath = process.env.CHROME_PATH || [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
].find(existsSync)
if (!browserPath) throw new Error('设置 CHROME_PATH 为 Chrome/Edge 可执行文件路径后运行浏览器验证。')
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const project = (index) => ({ Path: `E:\\历史快照\\项目${index}\\项目${index}.ap14`, ModifiedUtc: `2026-01-0${index}T12:00:00Z`, Length: 100 })
const projects = [project(1), project(2), project(3)]
const block = (status, index) => ({
  ObjectType: index === 1 ? 'TechnologicalObject' : 'Block',
  ExportId: status === 'Different' && index !== 1 ? 'a'.repeat(32) : null,
  LeftName: status === 'Removed' ? null : `设备控制/很长的程序块分组路径/FB_${index}`,
  RightName: status === 'Added' ? null : `设备控制/很长的程序块分组路径/FB_${index}`,
  ComparisonResult: status, LatestModifiedDate: status === 'Removed' ? null : '2026-01-03T13:00:00.1234567',
  PreviousModifiedDate: status === 'Added' ? null : '2026-01-02T12:00:00', Elements: [],
})
const comparison = (blocks) => [{ Device: 'PLC_1', Result: {
  LeftName: 'PLC_1', RightName: 'PLC_1', ComparisonResult: 'Different',
  LatestModifiedDate: null, PreviousModifiedDate: null, Elements: blocks,
} }]
const fixture = {
  Version: 3, GeneratedAtUtc: '2026-01-04T00:00:00Z', Projects: projects,
  Entries: [
    { PreviousProject: projects[0], LatestProject: projects[1], ComparedAtUtc: '2026-01-04T00:00:00Z', Comparisons: comparison([block('Identical', 0)]) },
    { PreviousProject: projects[1], LatestProject: projects[2], ComparedAtUtc: '2026-01-04T00:01:00Z',
      Comparisons: comparison(['Different', 'Added', 'Removed', ...Array(27).fill('Identical')].map(block)) },
  ],
}

const server = await createServer({ root, server: { host: '127.0.0.1', port: 0 }, logLevel: 'error' })
const profile = await mkdtemp(path.join(tmpdir(), 'central-admin-history-'))
let browser
let socket
let sessionId
let historyRequests = 0
let serviceLogRequests = 0
let serviceLogError = false
let serviceLogEmpty = false
let errorMode = false
let comparisonErrorMode = false
let comparisonRequests = 0
let reportRequests = 0
let sequence = 0
const pending = new Map()
const exceptions = []
const failures = []

function command(method, params = {}, session = sessionId) {
  const id = ++sequence
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)) }, 15000)
    pending.set(id, { resolve, reject, timer })
    socket.send(JSON.stringify({ id, method, params, ...(session ? { sessionId: session } : {}) }))
  })
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails))
  return result.result.value
}
async function waitFor(expression) {
  for (let count = 0; count < 100; count++) {
    if (await evaluate(expression)) return
    await sleep(100)
  }
  throw new Error(`UI condition timed out: ${expression}\nHistory requests: ${historyRequests}\nPage: ${await body()}\nExceptions: ${JSON.stringify(exceptions)}`)
}
const click = (selector) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`)
const body = () => evaluate('document.body.innerText')

try {
  await server.listen()
  const origin = `http://127.0.0.1:${server.httpServer.address().port}`
  browser = spawn(browserPath, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' })
  browser.on('error', (error) => failures.push(error))
  let lines
  for (let count = 0; count < 100; count++) {
    try { lines = (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).trim().split(/\r?\n/); break } catch { await sleep(100) }
  }
  assert.ok(lines, 'browser debugging endpoint started')
  socket = new WebSocket(`ws://127.0.0.1:${lines[0]}${lines[1]}`)
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }) })
  socket.addEventListener('message', ({ data }) => {
    const message = JSON.parse(data)
    if (message.id) {
      const job = pending.get(message.id)
      if (!job) return
      clearTimeout(job.timer); pending.delete(message.id)
      if (message.error) job.reject(new Error(JSON.stringify(message.error)))
      else job.resolve(message.result)
    } else if (message.method === 'Runtime.exceptionThrown') {
      exceptions.push(message.params.exceptionDetails)
    } else if (message.method === 'Fetch.requestPaused') {
      const { requestId, request } = message.params
      let status = 200
      let value = []
      let contentType = 'application/json'
      let rawBody
      if (request.url.includes('/GenerateChangesHistory')) {
        historyRequests++
        status = errorMode ? 503 : 200
        value = errorMode ? '模拟历史服务暂不可用' : fixture
      } else if (request.url.includes('/api/block-comparisons')) {
        if (request.method === 'OPTIONS') {
          status = 204
          rawBody = ''
        } else if (request.url.endsWith('/report')) {
          reportRequests++
          contentType = 'application/pdf'
          rawBody = '%PDF-1.4\n%%EOF'
        } else {
          if (request.method === 'POST') {
            comparisonRequests++
            assert.deepEqual(JSON.parse(request.postData), { ExportId: 'a'.repeat(32) })
          }
          value = { Id: 'b'.repeat(32), ExportId: 'a'.repeat(32),
            State: comparisonErrorMode ? 'Failed' : request.method === 'POST' ? 'Queued' : 'Succeeded',
            Message: comparisonErrorMode ? '模拟 ACTCLI 比较失败' : 'PDF 报告已生成', ExpiresUtc: '2030-01-01T00:00:00Z' }
        }
      } else if (request.url.includes('/GetEventLog')) {
        serviceLogRequests++
        status = serviceLogError ? 503 : 200
        const limit = Number(new URL(request.url).searchParams.get('maximumEntries'))
        value = serviceLogError ? '模拟日志服务不可用' : serviceLogEmpty ? [] : Array.from({ length: Math.min(60, limit) }, (_, index) => ({
          TimeCreated: '2026-01-03T13:00:00.1234567Z', User: 'DOMAIN\\operator', Computer: 'TIA-PC',
          EventContent: `<img src=x onerror=alert(1)>\n事件内容 ${index + 1}`, EventId: index + 1,
          Level: index % 2 ? '警告' : '信息', Provider: 'Siemens', RecordId: String(60 - index),
        }))
      } else if (request.url.includes('/api/discovery/scans')) {
        value = { items: [], totalCount: 0, page: 1, pageSize: 10 }
      }
      command('Fetch.fulfillRequest', {
        requestId, responseCode: status,
        responseHeaders: [{ name: 'Content-Type', value: contentType }, { name: 'Access-Control-Allow-Origin', value: '*' },
          { name: 'Access-Control-Allow-Methods', value: 'GET, POST, OPTIONS' }, { name: 'Access-Control-Allow-Headers', value: 'Content-Type' }],
        body: Buffer.from(rawBody ?? JSON.stringify(value)).toString('base64'),
      }).catch((error) => failures.push(error))
    }
  })
  const target = await command('Target.createTarget', { url: 'about:blank' }, null)
  sessionId = (await command('Target.attachToTarget', { targetId: target.targetId, flatten: true }, null)).sessionId
  await command('Runtime.enable')
  await command('Page.enable')
  await command('Network.enable')
  await command('Network.setBlockedURLs', { urls: ['*fonts.googleapis.com*', '*fonts.gstatic.com*'] })
  await command('Fetch.enable', { patterns: [
    { urlPattern: '*/GenerateChangesHistory' }, { urlPattern: '*/getTags' }, { urlPattern: `${origin}/api/*` },
    { urlPattern: '*/api/block-comparisons*' }, { urlPattern: '*/GetEventLog*' },
  ] })
  await command('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false })
  await command('Page.navigate', { url: `${origin}/#changes-history` })
  await waitFor("document.querySelectorAll('.history-entry').length === 2 && document.querySelectorAll('.history-table tbody tr').length === 3")
  assert.equal(historyRequests, 1, 'first entry loads history once')
  assert.equal(serviceLogRequests, 0, 'new log page is not prefetched')
  assert.equal(await evaluate("document.querySelectorAll('.compare-button').length"), 1, 'only modified ordinary blocks are clickable')
  await click('.compare-button')
  await waitFor("!!document.querySelector('.report-frame') && !!document.querySelector('.comparison-report a[download]')")
  assert.equal(comparisonRequests, 1)
  assert.equal(reportRequests, 1)
  const blobUrl = await evaluate("document.querySelector('.report-frame').getAttribute('src')")
  assert.ok(blobUrl.startsWith('blob:'), 'PDF is previewed through a local blob URL')
  await click('.comparison-report .refresh-button')
  await waitFor("!document.querySelector('.comparison-report')")
  assert.ok(await evaluate(`fetch(${JSON.stringify(blobUrl)}).then(() => false, () => true)`), 'closing revokes the PDF blob')
  comparisonErrorMode = true
  await click('.compare-button')
  await waitFor("document.querySelector('.comparison-report .error')?.innerText.includes('模拟 ACTCLI 比较失败')")
  comparisonErrorMode = false
  await click('.comparison-report .error button')
  await waitFor("!!document.querySelector('.report-frame')")
  await click('.comparison-report .refresh-button')
  assert.equal(await evaluate('document.title'), 'CentralAdmin · 更改历史')
  assert.ok((await body()).includes('项目3.ap14'), 'latest comparison selected')
  assert.ok((await body()).includes('13:00:00.1234567'), 'block timestamp precision retained')
  assert.ok(await evaluate("Array.from(document.querySelectorAll('.history-table tbody td')).some(cell => cell.textContent === '工艺对象')"), 'technology object type shown')
  await evaluate("document.querySelector('#history-object-type').value='TechnologicalObject'; document.querySelector('#history-object-type').dispatchEvent(new Event('change',{bubbles:true}))")
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 1")
  await evaluate("document.querySelector('#history-object-type').value=''; document.querySelector('#history-object-type').dispatchEvent(new Event('change',{bubbles:true}))")
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 3")
  console.log('PASS: direct hash navigation, latest selection, changed-only rows and timestamp precision')
  await evaluate("const input=document.querySelector('#history-search'); input.value='FB_1'; input.dispatchEvent(new Event('input',{bubbles:true}))")
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 1")
  await evaluate("document.querySelector('#history-search').value=''; document.querySelector('#history-search').dispatchEvent(new Event('input',{bubbles:true}))")
  await click('#history-only-changes')
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 25")
  await click('.history-detail .pager button:last-child')
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 5")
  await click('.history-entry:first-child')
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 1")
  await click('#history-only-changes')
  await waitFor("document.body.innerText.includes('所有对象的 ModifiedDate 均相同')")
  await click('.history-entry:last-child')
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 3")
  console.log('PASS: search, all/changed toggle, block pagination and unchanged comparison')
  await click('.sidebar a[href="#plc-tags"]')
  await waitFor("document.querySelector('#plc-tags') !== null")
  await click('.sidebar a[href="#changes-history"]')
  await waitFor("document.querySelectorAll('.history-table tbody tr').length === 3")
  assert.equal(historyRequests, 1, 'KeepAlive return uses cached snapshot')
  errorMode = true
  await click('.changes-dashboard .title-row button')
  await waitFor("document.body.innerText.includes('显示旧快照')")
  assert.equal(await evaluate("document.querySelectorAll('.history-table tbody tr').length"), 3)
  errorMode = false
  await click('.changes-dashboard .title-row button')
  await waitFor("!document.body.innerText.includes('显示旧快照') && !document.querySelector('.changes-dashboard .title-row button').disabled")
  assert.equal(historyRequests, 3)
  console.log('PASS: cached page return, failed refresh retains stale snapshot, successful manual retry')
  const artifacts = path.join(root, 'node_modules/.cache/history-validation')
  await mkdir(artifacts, { recursive: true })
  await writeFile(path.join(artifacts, 'desktop.png'), Buffer.from((await command('Page.captureScreenshot', { format: 'png' })).data, 'base64'))
  await command('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true })
  await sleep(200)
  assert.ok(await evaluate("getComputedStyle(document.querySelector('.mobile-navigation')).display !== 'none'"), 'mobile navigation visible')
  assert.ok(await evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'no horizontal page overflow on mobile')
  assert.ok(await evaluate("document.querySelector('.history-table-scroll').scrollWidth > document.querySelector('.history-table-scroll').clientWidth"), 'wide table scrolls inside panel')
  await click('.mobile-navigation a[href="#scan-history"]')
  await waitFor("document.querySelector('#scan-history') !== null")
  await evaluate("document.querySelector('.mobile-navigation a[href=\"#changes-history\"]').focus()")
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', windowsVirtualKeyCode: 13 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', windowsVirtualKeyCode: 13 })
  await waitFor("document.querySelector('#changes-history') !== null")
  assert.equal(historyRequests, 3)
  await writeFile(path.join(artifacts, 'mobile.png'), Buffer.from((await command('Page.captureScreenshot', { format: 'png' })).data, 'base64'))
  await click('.mobile-navigation a[href="#service-event-logs"]')
  await waitFor("document.querySelectorAll('.service-log-table tbody tr').length === 25")
  assert.equal(serviceLogRequests, 1)
  assert.equal(await evaluate('document.title'), 'CentralAdmin · ServiceV14 日志')
  assert.equal(await evaluate("document.querySelectorAll('.service-log-table th').length"), 8)
  assert.equal(await evaluate("document.querySelectorAll('.service-log-content img').length"), 0, 'event content is escaped, never rendered as HTML')
  assert.ok((await body()).includes('<img src=x onerror=alert(1)>'))
  assert.ok(await evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'log page does not overflow mobile viewport')
  assert.ok(await evaluate("document.querySelector('.service-log-table-scroll').scrollWidth > document.querySelector('.service-log-table-scroll').clientWidth"))
  await click('.service-log-panel .pager button:last-child')
  await click('.service-log-panel .pager button:last-child')
  await waitFor("document.querySelectorAll('.service-log-table tbody tr').length === 10")
  await evaluate("document.querySelector('#service-log-keyword').value='事件内容 60'; document.querySelector('#service-log-keyword').dispatchEvent(new Event('input',{bubbles:true}))")
  await waitFor("document.querySelectorAll('.service-log-table tbody tr').length === 1")
  await evaluate("document.querySelector('#service-log-keyword').value=''; document.querySelector('#service-log-keyword').dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('#service-log-level').value='warning'; document.querySelector('#service-log-level').dispatchEvent(new Event('change',{bubbles:true}))")
  await waitFor("document.querySelector('.service-log-panel .panel-subhead').innerText.includes('匹配 30 / 60')")
  await click('.mobile-navigation a[href="#operation-logs"]')
  await waitFor("document.querySelector('#operation-logs') !== null")
  await click('.mobile-navigation a[href="#service-event-logs"]')
  await waitFor("document.querySelectorAll('.service-log-table tbody tr').length === 25")
  assert.equal(serviceLogRequests, 1, 'returning retains cached logs and filters')
  serviceLogError = true
  await click('.service-logs-dashboard .title-row button')
  await waitFor("document.querySelector('.service-logs-dashboard').innerText.includes('显示旧快照')")
  assert.equal(await evaluate("document.querySelectorAll('.service-log-table tbody tr').length"), 25)
  assert.ok((await body()).includes('通道'))
  serviceLogError = false
  for (const limit of ['0', '5001', '1.5', '']) {
    await evaluate(`document.querySelector('#service-log-limit').value=${JSON.stringify(limit)}; document.querySelector('#service-log-limit').dispatchEvent(new Event('input',{bubbles:true}))`)
    await waitFor("document.querySelector('.service-logs-dashboard .title-row button').disabled")
  }
  assert.equal(serviceLogRequests, 2, 'invalid limits do not send requests')
  await evaluate("document.querySelector('#service-log-level').value='all'; document.querySelector('#service-log-level').dispatchEvent(new Event('change',{bubbles:true})); document.querySelector('#service-log-limit').value='1'; document.querySelector('#service-log-limit').dispatchEvent(new Event('input',{bubbles:true}))")
  await click('.service-logs-dashboard .title-row button')
  await waitFor("document.querySelectorAll('.service-log-table tbody tr').length === 1 && !document.querySelector('.service-logs-dashboard .title-row button').disabled")
  serviceLogEmpty = true
  await click('.service-logs-dashboard .title-row button')
  await waitFor("document.querySelector('.service-logs-dashboard').innerText.includes('暂无日志记录')")
  await writeFile(path.join(artifacts, 'service-logs-mobile.png'), Buffer.from((await command('Page.captureScreenshot', { format: 'png' })).data, 'base64'))
  await command('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false })
  await command('Page.navigate', { url: `${origin}/#service-event-logs` })
  await waitFor("document.querySelector('.service-logs-dashboard')?.innerText.includes('暂无日志记录')")
  assert.equal(await evaluate('document.title'), 'CentralAdmin · ServiceV14 日志')
  console.log('PASS: ServiceV14 direct hash, eight text-only fields, filters, pagination, cached return, stale snapshot, invalid limits, empty logs and mobile layout')
  assert.equal(exceptions.length, 0, `no browser runtime exceptions: ${JSON.stringify(exceptions)}`)
  assert.equal(failures.length, 0, `no mock transport errors: ${failures}`)
  console.log('PASS: mobile navigation, keyboard activation, internal table scrolling, no viewport overflow or runtime exceptions')
  console.log(`ALL BROWSER VALIDATIONS PASSED (mock backend); screenshots: ${artifacts}`)
} finally {
  if (socket?.readyState === WebSocket.OPEN) {
    await command('Browser.close', {}, null).catch(() => {})
    socket.close()
  }
  for (const job of pending.values()) { clearTimeout(job.timer); job.resolve({}) }
  browser?.kill()
  await server.close()
  await sleep(300)
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {})
}
