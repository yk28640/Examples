<script setup>
import { onBeforeUnmount, ref, watch } from 'vue'
import { createBlockComparison, getBlockComparison, getBlockReport, waitForPoll } from './api/blockComparisons'

const props = defineProps({ sourceId: String, exportId: String, blockName: String })
defineEmits(['close'])
const busy = ref(false)
const message = ref('')
const error = ref('')
const reportUrl = ref('')
const taskId = ref('')
let controller = new AbortController()

function release() {
  controller?.abort()
  if (reportUrl.value) URL.revokeObjectURL(reportUrl.value)
  reportUrl.value = ''
}

async function compare() {
  release()
  const current = new AbortController()
  controller = current
  const sourceId = props.sourceId
  const exportId = props.exportId
  busy.value = true
  message.value = '正在提交后端比较任务…'
  error.value = ''
  taskId.value = ''
  try {
    let task = await createBlockComparison(sourceId, exportId, current.signal)
    const deadline = Date.now() + 10 * 60 * 1000
    while (true) {
      if (current.signal.aborted) return
      if (task.ExportId !== exportId || (taskId.value && taskId.value !== task.Id)) throw new Error('比较任务与所选程序块不匹配。')
      taskId.value = task.Id
      message.value = task.Message
      if (task.State === 'Failed') throw new Error(task.Message)
      if (task.State === 'Succeeded') break
      if (Date.now() > deadline) throw new Error('已等待 10 分钟，后端可能仍在运行。请稍后点击重试以重新查询同一任务。')
      await waitForPoll(current.signal)
      task = await getBlockComparison(sourceId, task.Id, current.signal)
    }
    const pdf = await getBlockReport(sourceId, task.Id, current.signal)
    if (!current.signal.aborted) reportUrl.value = URL.createObjectURL(pdf)
  } catch (exception) {
    if (!current.signal.aborted) error.value = exception instanceof Error ? exception.message : '比较请求失败，请重试。'
  } finally {
    if (controller === current) busy.value = false
  }
}

watch(() => [props.sourceId, props.exportId], compare, { immediate: true })
onBeforeUnmount(release)
</script>

<template>
  <section class="panel comparison-report" aria-label="程序块差异报告" :aria-busy="busy">
    <div class="panel-header">
      <h2>程序块差异 · {{ blockName }}</h2>
      <button type="button" class="refresh-button" @click="$emit('close')">关闭报告</button>
    </div>
    <div class="report-status">
      <p role="status" aria-live="polite">{{ message }}</p>
      <p v-if="error" class="notice error" role="alert">{{ error }} <button type="button" :disabled="busy" @click="compare">重试</button></p>
      <p v-if="busy">离开页面仅取消浏览器等待，不会停止后端比较。</p>
      <a v-if="reportUrl" :href="reportUrl" :download="`comparison-${taskId}.pdf`">下载 PDF（浏览器不支持预览时使用）</a>
    </div>
    <iframe v-if="reportUrl" :src="reportUrl" title="程序块新旧版本差异 PDF" class="report-frame"></iframe>
  </section>
</template>

<style scoped>
.comparison-report{margin:24px 0;overflow:hidden}
.panel-header{gap:16px;flex-wrap:wrap}
.panel-header h2{overflow-wrap:anywhere}
.report-status{padding:16px 24px;line-height:1.8;overflow-wrap:anywhere}
.report-status a{color:#8be4bc}
.report-frame{display:block;width:100%;height:70vh;min-height:360px;border:0;background:white}
</style>
