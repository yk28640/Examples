# CentralAdmin

Vue 3 + Vite 工业监控看板，统一读取 AutomationPlatform 的只读 GET API。本界面不能发起设备扫描或写入 PLC 变量。

## 页面

- 扫描历史（`#scan-history`）：扫描批次与设备快照。
- 操作日志（`#operation-logs`）：最新 100 条 TIA Portal 操作日志。
- PLC 变量（`#plc-tags`）：先选择设备来源，再读取该来源的变量集合。表格显示设备、分组、变量表、名称、数据类型与地址，不包含实时变量值。

变量页面支持跨字段关键词搜索、设备/分组/变量表/数据类型组合筛选，每页 25 条，空分组显示为“未分组”。切换来源会取消旧请求，重置筛选及分页；已成功读取的来源按来源 ID 在当前页面会话内缓存，切回不会自动请求，点击“刷新当前来源”更新。不同来源分别保存数据、成功读取时间及错误状态，刷新失败时明确标记缓存不是最新结果，不会将失败伪装为“暂无变量”。重新加载浏览器会清除缓存。

使用侧边栏切换页面，小屏幕使用顶部导航；支持 Hash 地址直达。切换到扫描历史或操作日志页面后，变量页面的当前来源及筛选会保留。

## 设备来源配置

在前端 `src/config/tagSources.js` 的 `tagSources` 数组中维护来源：

```js
export const tagSources = [
  {
    id: 'plc-01',
    name: 'PLC 01',
    baseUrl: 'http://172.16.0.100:5736',
  },
]
```

增加设备时追加配置项，设置不同的 `id`、显示名称和服务地址。开发模式下 Vite 热更新会刷新配置；部署前修改配置后重新构建前端。变量页在浏览器中直接请求每个来源的 `${baseUrl}/getTags`，不依赖 AutomationPlatform 的 `TagSources` 配置或 `/api/tag-sources` 接口。

- `id`：稳定来源标识，1 到 64 位字母、数字、下划线或连字符，首位为字母或数字；大小写不敏感且不得重复。不要用接口返回的 `Device` 名称作为来源 ID。
- `name`：非空的来源显示名称。
- `baseUrl`：浏览器可访问的 HTTP/HTTPS 服务地址；前端在其路径后追加 `/getTags`。不能包含用户凭据、查询参数或片段。此配置会随前端代码公开，不要在其中放置秘密。

标签接口必须直接返回 JSON 数组，每个元素包含 `Device`、`Group`、`Table`、`Name`、`DataType`、`Address` 六个字符串字段；空字符串允许，缺失字段、null 或非字符串字段会报告数据格式异常。浏览器跨域请求要求标签服务允许 CentralAdmin 所在源的 CORS GET 请求。

## 本地启动

1. 启动 `AutomationPlatform` 的 `http` 配置，确保 API 监听 `http://localhost:5123`。
2. 在此目录运行 `npm install`、`npm run dev`，打开终端输出的前端地址（默认 `http://localhost:5173`）。Windows PowerShell 若限制 npm.ps1，请使用 `npm.cmd install`、`npm.cmd run dev`。
3. 扫描历史和操作日志通过 Vite 将 `/api` 代理至后端。PLC 变量由浏览器直接请求前端来源配置中的 `/getTags`，因此标签服务需允许开发页面源的 CORS GET 请求。
4. 扫描历史来自 `/api/discovery/scans`，操作日志来自 `/api/event-logs/operational`。没有扫描记录时，请先在 AutomationPlatform 执行扫描，并检查 `Discovery:ScanHistoryDatabasePath`；操作日志要求后端 Windows 主机具有相应日志通道及读取权限。

## 部署

运行 `npm run build`，将 `dist/` 交给静态文件服务器部署，并将同源 `/api` 反向代理至 AutomationPlatform。扫描历史和操作日志使用 `VITE_API_BASE_URL`（API 根地址，不包含 `/api`），可在构建前配置；PLC 变量来源独立维护在 `src/config/tagSources.js`，浏览器直接请求标签服务，因此生产标签服务也需允许部署站点源的 CORS GET 请求。Vite 开发代理不包含在构建产物内。

变量来源配置位于前端项目中并会公开给浏览器用户；不要在来源地址中嵌入凭据。浏览器标签请求超时为 75 秒。

只读 API 目前匿名开放，部署时请限制可访问的网络范围；来源地址应只由可信管理员维护。
