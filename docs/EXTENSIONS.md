# 自定义插件

总管家从 `运行数据目录/extensions/packages/` 发现插件。每个插件使用独立文件夹：

```text
packages/
└─ weather-demo/
   ├─ plugin.json
   ├─ weather-demo.exe
   └─ weather-demo.dll   # 如果程序自身需要
```

`plugin.json` 示例：

```json
{
  "schemaVersion": 1,
  "id": "example.weather",
  "name": "天气查看",
  "version": "1.0.0",
  "publisher": "your-name",
  "description": "显示用户主动查询的天气。",
  "entry": "weather-demo.exe",
  "entrySha256": "可选的64位十六进制SHA-256",
  "arguments": [],
  "capabilities": ["network"]
}
```

支持的能力声明为 `network`、`filesystem-read`、`filesystem-write`、`location`、
`microphone`、`camera` 和 `child-process`。能力声明用于把风险告诉用户，不是 Windows
权限沙盒。插件仍以当前 Windows 用户身份运行。

## 使用方法

1. 在总管家“自定义插件”页点击“打开插件目录”。
2. 把完整插件文件夹放进 `packages`，不要只复制一个依赖不完整的 EXE。
3. 点击“刷新插件”。插件初次发现时默认关闭。
4. 阅读发布者、能力和文件指纹后手动启用，再点击运行。
5. 插件目录内任意文件变化，旧授权自动失效，需要重新确认。

## 安全边界

- 只执行插件目录内的相对 `.exe`，拒绝绝对路径、`..`、UNC、符号链接和目录联接。
- 参数使用 Windows 进程参数列表逐个传递，不经过 PowerShell、CMD 或 Shell 拼接。
- 插件在独立子进程中运行；退出码、标准输出和错误输出显示在插件页。
- 关闭总管家会请求结束由本次总管家启动的插件进程树。
- 插件获得独立数据目录；总管家不会主动传入 Codex、OAuth、API Key、号池或服务器配置。
- 总管家不是安全沙盒。第三方插件可能主动读取当前用户可访问的文件或发起网络请求。
  不要启用来源不明的可执行文件；需要强隔离时应在 Windows Sandbox 或虚拟机中运行。

## 开发约定

插件不是总管家 DLL，也没有不稳定的内部 .NET API。任何语言只要能生成独立 Windows
EXE 都可以使用。可读取的非敏感环境变量：

- `CMM_EXTENSION_ID`
- `CMM_EXTENSION_NAME`
- `CMM_EXTENSION_ROOT`
- `CMM_EXTENSION_DATA_DIR`
- `CMM_EXTENSION_CAPABILITIES`

插件应把持久数据写入 `CMM_EXTENSION_DATA_DIR`，把适合用户查看的内容写到标准输出，
失败时使用非零退出码。插件自行负责其网络请求、隐私说明和第三方依赖安全。
