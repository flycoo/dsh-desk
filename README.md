# DSH Desk

DSH Desk 是 DeepSeek Harness 官方 Web 界面的 Windows 桌面壳。它使用 WPF 和 WebView2，负责启动本机官方 DSH、显示服务状态并提供系统托盘控制。

## 开发

```powershell
dotnet build .\DshDesk.slnx
dotnet run --project .\tests\DshDesk.Tests\DshDesk.Tests.csproj
```

## 发布

```powershell
dotnet publish .\src\DshDesk\DshDesk.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\DSHDesk-win-x64
```

程序默认使用：

- DSH Home：`G:\DeepSeekHarness\.dsh-home`
- npm 缓存：`G:\DeepSeekHarness\.npm-cache`
- DSH Desk 数据：`G:\DeepSeekHarness\.dsh-desk`

DSH Desk 只加载 `127.0.0.1` 上实际启动或探测到的 DeepSeek Harness 页面。外部链接会交给系统默认浏览器。
