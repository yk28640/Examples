# Copilot Instructions

## 项目指南
- 在此用户的 TIA Portal V14 SP1 Openness 环境中，一个 TiaPortal 实例只能同时打开一个项目；比较两个离线工程时应使用不同的 TiaPortal 实例分别打开。
- 更改历史 GenerateChangesHistory 必须复用服务端 ChangesHistory.json 和内存缓存；不能因每次 API 请求而重新打开 TIA 工程或重新比较/导出。