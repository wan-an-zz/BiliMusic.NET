# MusicProbe —— 音乐服务真机验证探针

单元测试证明"按假响应能跑对"；本探针回答另一个问题：**真实 B 站接口 + BASS 网络流是否能播放**。
它用两首默认曲目走一遍载入、播放、暂停、切歌、指定曲、替换与删除，逐步断言并汇总。

默认曲目：`BV1aJaz6yEgq`、`BV1cupJ6EEff`。

## 怎么跑

~~~powershell
# 首次：下载并部署 BASS 原生库
pwsh -File tools/fetch-bass.ps1

# 离线自检：假 HTTP + 假引擎，不联网、不需要 bass.dll
dotnet run --project tools/MusicProbe -- --offline

# 真机播放
dotnet run --project tools/MusicProbe -- --seconds 5

# 排查建流失败：逐个尝试请求头/缓冲组合
dotnet run --project tools/MusicProbe -- --diagnose
~~~

真机模式需要 Windows 声卡、`bass.dll` 与可访问 B 站/CDN 的网络；无声音环境下 `Bass.Init` 会失败并以退出码 2 结束。

## 选项

| 选项 | 作用 |
| --- | --- |
| `--offline` | 离线自检：用假响应与假引擎跑同一套编排 |
| `--diagnose` | 只诊断 BASS 建流，尝试不同头/缓冲组合并下载本地文件验证解码 |
| `--bv <BV>` | 指定曲目，可重复；默认两首 |
| `--seconds <秒>` | 观察播放进度的时长，默认 6 |
| `--timeout <秒>` | 整个流程超时，默认 120 |
| `--proxy <地址>` | 显式代理；同时传给 HttpClient 与 BASS |
| `--wait-end` | 等待当前曲目自然播完以验证自动切歌（可能数分钟） |
| `--url <地址>` | 诊断模式直接使用该地址，跳过 B 站解析 |
| `--verbose` | 打印服务内部日志 |
| `-h, --help` | 帮助 |

## 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 全部断言通过 |
| 1 | 有断言失败 |
| 2 | 基础设施失败（原生库/网络/声卡/取消/超时） |
| 64 | 参数用法错误 |

## 断言清单（真机）

| # | 断言 | 失败通常意味着 |
| --- | --- | --- |
| 1 | 列表顺序与输入一致 | 插入逻辑或重复 BV 处理有问题 |
| 2 | 自动播放第一首 | 地址解析或 BASS 建流失败 |
| 3 | 状态进入 Playing | `Bass.Init`、声卡或解码失败 |
| 4 | 播放位置在推进 | 流卡住、CDN 拒绝或缓冲异常 |
| 5 | 下一首/上一首/暂停/继续成功 | 播放控制或引擎状态错误 |
| 6 | 指定曲下一首分支移动正确 | `PlayPlaylistMusic` 顺序错误 |
| 7 | 删除当前曲目被拒绝 | 保护逻辑回归 |
| 8 | 整表替换/空列表状态正确 | `ReplacePlaylistAsync` 回归 |

离线自检额外覆盖自然播完自动切歌、预取、删除保护与空表替换，共 25 项断言。

## 为什么要用 durl

`playurl` 的 DASH 音频是分片 `m4s`。实测 BASS 的 `BASS_StreamCreateURL` 对这类 URL 一律返回 `FileFormat`，
同一文件下载到本地却能用 `BASS_StreamCreateFile` 播放；换成 `fnval=1` 的 `durl` 顺序 MP4 后 URL 流正常。
因此服务请求 `qn=80&fnval=1`，匿名状态下会降级到 720P，音轨约 212 kbps。

`--diagnose` 会把同样地址分别按不同请求头与 `Default/StreamDownloadBlocks/Prescan/RestrictDownloadRate`
组合建流，并下载到本地用文件流对照，便于以后 BASS 版本变化时复现结论。

## 原生库

- Windows：`native/win-x64/bass.dll`，构建时复制到输出根目录。
- Android：由仓库根脚本复制四个 ABI 的 `libbass.so` 与 `libbass_ssl.so`，探针本身不构建 Android。
- 原生库目录被 `.gitignore` 忽略，不提交二进制。
