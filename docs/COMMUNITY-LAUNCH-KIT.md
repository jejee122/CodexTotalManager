# Codex 总管家：GitHub 与社区发布工具包

这份文件用于公开发布前最后检查和首周推广。它不是“保证 Stars”的承诺；目标是让真正需要
Codex 多模型、多账号和本机控制面的用户能快速理解、验证并反馈。

## GitHub 仓库建议

- 建议仓库名：`Codex-Total-Manager`
- About：`Windows 上的 Codex 多模型、独立账号出口、皮肤、Worker 与本机网关控制面。默认不接管 Codex。`
- Website：首周可留空，避免指向尚未准备的页面
- Topics：`codex`、`openai`、`windows`、`wpf`、`model-router`、`responses-api`、`account-pool`、`ai-tools`
- 勾选 Issues；Discussion 可在有维护时间后再开
- 不要在仓库名和 About 中使用“官方”“OpenAI 官方项目”等误导性描述

现有空仓库名 `jejjeeee` 不利于搜索和记忆。正式上传前建议在 GitHub Settings 中改名，旧地址通常会自动重定向。

## 上传前硬门槛

1. 项目所有者选择根许可证，并添加 `LICENSE`；
2. 再跑一次源码敏感信息扫描；
3. 确认 Git 中没有 `out/`、`bin/`、`obj/`、`deployment/runtime-v3/`、`evidence/`、安装包和第三方 EXE；
   `deployment/` 中只保留构建必需、已经审计的两个启动脚本；
4. README 首页至少放 2 张真实截图：主页连接状态、Codex 原生模型列表；
5. 截图遮住账号、路径、服务器、Token、IP 和用量身份；
6. 创建 `v3.0.0-rc.28` Release，明确写“候选版，不建议生产使用”；
7. Release 附 SHA-256、已跑测试、未跑真实验收和已知问题；
8. 实际上传账号必须是仓库所有者或有 Write 权限的 Collaborator。

## GitHub Release 文案

### 标题

`Codex Total Manager 3.0.0-rc.28 — Windows 软件中心、通用插件、原生模型目录与无重启切换候选版`

### 正文

这是 Codex 总管家的首个公开候选版，面向需要在 Windows 上统一管理 Codex 模型、独立账号出口、
皮肤、Worker 和本机服务状态的用户。

rc.28 的核心变化：

- 保留 Codex 内置 `openai` 身份，通过 `openai_base_url` 接入本机 Native Engine；
- 使用 `model_catalog_json` 把第三方模型放进 Codex 原生模型菜单；
- 删除正常切换路径中的自动 UI 点击和自动 Codex 重启；
- 增加 Responses 对话续接，跨协议切换时展开上一轮消息；
- 增加 `/readyz`，不再把“端口活着”当成“模型和路由已可用”；
- Native Engine 不再强制依赖 v2rayN 固定端口；
- 连接、断开、旧 `cmm_native` 迁移和模型缓存采用所有权标记与失败关闭。

验证：全解决方案 0 错误/0 警告、当前安全测试集全部通过（数量以实际测试输出为准）、隔离集成矩阵、10 万条账本压力测试，
以及假 Chat/Responses/Anthropic/Google 上游和第三方统一网关端到端检查均通过。当前工作区尚未生成新的 rc.28 安装包。

尚未完成：真实 Codex、真实 OAuth 多账号、皮肤和真实扣费账号仍需专用测试电脑验收。
请先在非生产环境使用，并在 Issue 中提交脱敏反馈。

## LinuxDo 首发草稿

### 标题

`[开源候选] 做了一个 Windows Codex 总管家：原生模型菜单、独立账号出口、皮肤和 Worker，默认不接管 Codex`

### 正文

最近在多模型和多账号之间切 Codex，最难受的不是 API 转发本身，而是模型菜单、账号出口、
聊天连续性和本机脚本各管一摊。于是做了一个 WPF 控制面，把这些能力放到一个地方。

这版比较在意的几个点：

1. 默认断开，只有手动确认才接入 Codex；
2. 保留 Codex 内置 `openai` 身份，用官方支持的模型目录把 `provider/model` 放进原生菜单；
3. 不自动点击或重启 Codex，当前任务由用户在原生菜单里选择；
4. 第三方协议不认识 `previous_response_id` 时，在本机展开上一轮消息，避免切模型突然失忆；
5. 每个 CLIProxyAPI 账号出口独立端口和凭据槽，真实扣费仍以下一条请求日志为准；
6. 另外集成了皮肤、Worker 预算审计、本机服务和服务器状态面板。

现在是 rc 候选版，不是生产稳定版。隔离测试和 10 万条账本压力测试过了，真实 OAuth/皮肤/扣费账号
还在专用电脑验收。欢迎先看架构和安全边界，尤其想听听大家对“原生模型菜单”和“账号不串池”的需求。

GitHub：`发布后填写`

如果你愿意测试，请不要在回帖或 Issue 里贴 Token、Cookie、账号文件或完整日志。

## V2EX 首发草稿

### 标题

`[分享创造] Codex Total Manager：Windows 上的多模型、独立账号出口和本机控制面`

### 正文

做了一个 Windows WPF 项目，用来统一管理 Codex 的模型目录、独立账号出口、皮肤、外部 Worker 和
本机服务状态。

它不是 Codex 替代客户端。默认情况下完全不接管 Codex；用户确认后才写入本机网关和模型目录，
断开时只删除自己拥有的配置。rc.28 取消了自动点击模型菜单和自动重启 Codex，第三方模型通过
`provider/model` 进入原生列表，并补了 Responses 跨协议对话续接。

目前是候选版，真实 OAuth、皮肤和扣费账号还在独立测试电脑验收。README 里列了端口、隐私边界、
架构图、测试范围和未完成项。欢迎做代码审阅或提交脱敏后的兼容性反馈。

GitHub：`发布后填写`

## 首周节奏

- 第 0 天：许可证、敏感扫描、真实截图、Release SHA-256 全部完成；
- 第 1 天：GitHub 发布，先邀请 3–5 位真正使用 Codex 的朋友审阅 README 和安装步骤；
- 第 2 天：根据首批问题修 README，不急着四处复制粘贴；
- 第 3 天：LinuxDo 发布技术细节版，持续回复可复现问题；
- 第 4 天：补一段 30–60 秒真实演示 GIF，展示“连接 → 原生模型列表 → 断开”；
- 第 5 天：V2EX 发布更短的产品说明，明确候选状态；
- 第 6 天：整理首批 Issue，发布兼容性表和已知问题；
- 第 7 天：发一篇复盘，展示修了什么，不虚报下载量或稳定性。

避免刷屏、互赞群和夸大“原生支持”。首周最有价值的不是空 Star，而是 5–10 个能复现、能帮助完善
真实 Codex 兼容性的用户。
