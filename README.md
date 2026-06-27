# Server Emulator

2D 横版动作游戏服务端模拟器，用于研究学习网络游戏服务端架构与协议。

## 协作

目前项目仍处于研究和开源完善阶段，还存在非常多 BUG。欢迎有能力的朋友一起提交 Issue、PR，或加入 Discord 共同完善服务端、客户端适配、封包、数据库和 PVF 相关内容。

## 社区交流

Discord 社区: https://discord.gg/3wct6SZp

## 补丁源码

补丁源码位于 `Patch/` 目录。补丁成品已经生成好，并且已经放在客户端中；普通用户直接使用客户端即可，不需要重新编译补丁。

## 快速启动

**仅下载仓库源码的普通用户，目前无法直接运行服务端。** 仓库不再附带预编译输出；需要先自行构建，或下载预编译压缩包。

### 使用预编译压缩包

解压后使用启动脚本运行服务端（压缩包内已包含）：

- Windows: `start-server.bat`
- macOS / Linux: `./start-server.sh`

1. 将客户端的 `Script.pvf` 放到解压后文件夹内的 `Data/Pvf/`
2. 在本机测试可直接运行启动脚本（默认 `127.0.0.1`）
3. 虚拟机或局域网连接时，使用 `--server-ip auto` 自动检测本机 LAN IP，或手动指定：

```bash
./start-server.sh --server-ip auto
./start-server.sh --server-ip 192.168.0.63
```

Windows:

```bat
start-server.bat --server-ip auto
start-server.bat --server-ip 192.168.0.63
```

macOS 首次从浏览器下载后若提示无法验证开发者，在解压目录执行：

```bash
xattr -dr com.apple.quarantine .
```

### 从源码构建

```bash
./publish.sh          # macOS / Linux
publish.bat           # Windows
./start-server.sh     # macOS / Linux
start-server.bat      # Windows
```

虚拟机或局域网连接时：

```bash
./start-server.sh --server-ip auto
./start-server.sh --server-ip 192.168.0.63
```

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)（提供 `dotnet` CLI）。NuGet 依赖自动下载，无需额外配置。

```bash
dotnet build Server/DfoServer.sln -c Debug
```

或双击 `build.bat`。开发调试可用 `dotnet run`；给最终用户分发请用 `publish.sh` / `publish.bat` 生成自带运行时的自包含包（输出到 `dist/`）。

## 运行

1. 将客户端的 `Script.pvf` 放到运行目录下的 `Data/Pvf/`
2. 启动服务端：
   - **预编译压缩包**：`./start-server.sh` 或 `start-server.bat`
   - **源码仓库**：`./start-server.sh` / `start-server.bat`（会自动查找 `dist/<平台>/` 或 Debug 构建）
3. 服务端监听 7001 (Channel) + 10011 (Game) 端口

## 数据库

种子数据库位于源码 `Server/DfoServer/Data/inventory.db`，发布时会复制到输出目录。
首次运行时自动创建表结构。附带的 DB 包含一个种子角色。

## 项目结构

```
Server/DfoServer/    服务端主程序 (.NET 10)
Tool/PvfLib/         PVF 档案解析库
publish.sh / publish.bat   发布自包含包（输出到 dist/）
start-server.sh / start-server.bat   启动脚本（源码仓库与预编译压缩包）
cleanup.sh / cleanup.bat   清理构建输出
build.bat            开发构建脚本
```
