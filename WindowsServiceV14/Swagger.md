# Swagger 使用与部署

## 启用与关闭

项目继续使用 .NET Framework 4.6.2、Web API 2 和 OWIN 自托管；Swagger 使用 Swashbuckle.Core 5.6.0，不需要 IIS 或 ASP.NET Core。

源配置位于 App.config，部署后的配置位于服务可执行文件旁的 WindowsServiceV14.exe.config。在 appSettings 中将 SwaggerEnabled 设置为 true，然后重启 Windows 服务（Debug 控制台模式需停止后重新启动）。默认值为 false；配置项缺失或无法解析为布尔值时也关闭。设置 false 并重启后，Swagger 文档、UI 和静态资源路由均不再注册。该开关不是热更新配置。

## 访问地址

沿用 tagsconfig.json 中 listenIp 配置及现有 5736 端口，例如 listenIp 为 localhost 时：

- UI：http://localhost:5736/swagger（重定向到 /swagger/ui/index）
- Swagger 2.0 JSON：http://localhost:5736/swagger/docs/v1

Swagger UI 的 CSS、JavaScript 等由程序集内嵌资源提供，不依赖在线 CDN。不改变服务监听范围，也不额外开放端口。

## 发布

1. 还原 NuGet 包并构建所需配置。
2. 停止原 Windows 服务，部署完整构建输出，包括 Swashbuckle.Core.dll 及现有 OWIN、Web API、Newtonsoft.Json 等依赖；不要仅复制主程序。
3. 保留现场有效的 tagsconfig.json、工程和比较数据；更新 exe.config 时保留构建生成的 assemblyBinding 重定向及其他现场配置。
4. 根据需要修改 SwaggerEnabled，启动服务并检查上述地址。

## 接口与安全

文档包含现有七个接口，说明参数、主要状态码、任务状态及 PDF 文件响应。文档生成只读取路由和类型元数据，不调用控制器方法，也不打开 TIA 工程、比较工程或导出文件。现有 GenerateChangesHistory 的 ChangesHistory.json 和内存缓存实现没有改变。

Try it out 会真实发出请求：读取标签可能打开工程，离线比较及历史生成可能耗时并产生导出文件，创建比较任务可能启动后端比较。不要在未经确认的生产环境执行这些操作。

配置开关不等于身份验证或访问控制。当前 Swagger 沿用现有服务的网络访问边界，能够访问监听地址的客户端在启用时也能查看文档；请使用可信网络、防火墙等现有访问控制，生产环境不需要时保持关闭。工程路径等信息可能出现在接口响应中，不应公开到互联网。

## 已执行验证

- Debug 与 Release 构建成功；原项目的重复 Compile 项会产生 CS2002 警告，与本次 Swagger 接入无关。
- 使用临时内存 OWIN 宿主验证，不启动真实 Windows 服务、不占用 HTTP 端口、不调用 TIA 业务接口。
- false、配置缺失、无效值：Swagger 入口、JSON、UI、CSS 和 JavaScript 返回 404。
- true：入口返回重定向，JSON、UI、CSS 和 JavaScript 返回 200；文档包含全部七个接口，ExportId 请求模型、任务响应和 PDF 文件模型通过检查。
- 现场 Windows 服务账户、HTTP URL 访问权限及浏览器端交互仍需在实际部署环境确认。
