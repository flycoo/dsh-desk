# DSH Desk

DSH Desk 是 DeepSeek Harness 官方 Web 界面的 Windows 桌面壳。它使用 WPF 和 WebView2，负责启动本机官方 DSH、显示服务状态并提供系统托盘控制。

DSH Desk 不会下载或更新 DSH。使用前请先安装官方 npm 包：

```powershell
npm install --global @deepseek-ai/dsh
```

默认情况下，DSH Desk 会先连接 `127.0.0.1:3080` 上已经运行的 DeepSeek Harness，再从 PATH 和 npm 全局目录识别系统安装。也可以在设置中切换到“使用指定安装”，选择包含 `package.json` 的 `@deepseek-ai/dsh` 包目录。

启动端口按以下优先级选择：

1. **当前运行实例**：`127.0.0.1:3080`（或配置文件中 `AttachPort` 指定的端口）上已有健康的 DeepSeek Harness 时，直接连接，不启动新进程；
2. **配置端口**：没有运行实例时，使用配置文件（`%LOCALAPPDATA%\DSHDesk\settings.json`）中 `AttachPort` 指定的端口自行启动 DSH；配置文件未指定端口时默认 `3080`；
3. **随机端口**：配置端口已被其他程序占用时，改用 `--port 0`，由系统分配一个空闲端口。

## 开发

```powershell
dotnet build .\DshDesk.slnx
dotnet run --project .\tests\DshDesk.Tests\DshDesk.Tests.csproj
```

## 发布

```powershell
dotnet publish .\src\DshDesk\DshDesk.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\DSHDesk-win-x64
```

DSH Desk 自身数据保存在：

- 设置：`%LOCALAPPDATA%\DSHDesk\settings.json`
- 日志：`%LOCALAPPDATA%\DSHDesk\logs`
- WebView2 数据：`%LOCALAPPDATA%\DSHDesk\webview2`

DSH Desk 不覆盖 `DSH_HOME` 或 `npm_config_cache`，因此 DSH 会继承用户现有环境；未设置 `DSH_HOME` 时使用官方默认目录。DSH Web 的默认 workspace 是 `%USERPROFILE%`，可在设置中选择其他已经存在的目录。

DSH Desk 只加载 `127.0.0.1` 上实际启动或探测到的 DeepSeek Harness 页面。外部链接会交给系统默认浏览器。
