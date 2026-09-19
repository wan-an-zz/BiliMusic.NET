# Bilibili 公开 Web API 使用参考

一份独立、可迁移的 Bilibili（B 站）公开 Web 接口参考，覆盖 **音频提取 / 搜索 / 视频元数据 / 榜单 / 扫码登录** 五类场景。

面向"把 B 站视频当音频源播放"的需求：搜索或榜单拿到稿件 → 取详情得到 `cid` → 从 `playurl` 的 DASH 流里取出独立音频 → 直接交给播放器。

> 本文所有接口均为 B 站 Web 端自身使用的公开接口，无需 App 签名（`appkey`/`sign`），但部分接口要求 **WBI 签名** 与 **登录 Cookie**。
> 接口随时可能变更或加风控，请以线上实测为准。
>
> **本文件为独立通用文档，不依赖任何特定代码库，可直接复制到其他项目使用。**

---

## 目录

- [1. 域名与基础设施](#1-域名与基础设施)
- [2. 认证体系](#2-认证体系)
- [3. WBI 签名算法](#3-wbi-签名算法)
- [4. 接口总览](#4-接口总览)
- [5. 接口详情](#5-接口详情)
- [6. 音频流提取](#6-音频流提取)
- [7. 扫码登录流程](#7-扫码登录流程)
- [8. 统一约定与错误处理](#8-统一约定与错误处理)
- [9. 跨域与反爬](#9-跨域与反爬)
- [10. 最小可用示例](#10-最小可用示例)
- [11. 已知坑位清单](#11-已知坑位清单)

---

## 1. 域名与基础设施

| 域名 | 用途 |
| --- | --- |
| `https://api.bilibili.com` | 全部公开数据接口 |
| `https://passport.bilibili.com` | 登录 / 扫码相关 |
| `https://www.bilibili.com` | 视频页面；亦为所有接口的 `Referer` 取值 |
| `https://space.bilibili.com/{mid}/video` | UP 主投稿接口的 `Referer` 取值 |
| `*.hdslb.com` | 封面图 CDN（`i0.hdslb.com` / `i1.hdslb.com` / `i2.hdslb.com`） |
| `*.bilivideo.com`、`*.bilivideo.cn`、`*.akamaized.net` | 音视频流 CDN（`playurl` 返回的 `baseUrl` 落在这些域） |

### 请求头要求

所有请求建议携带：

```
Referer: https://www.bilibili.com
Origin:  https://www.bilibili.com
User-Agent: <现代桌面浏览器 UA>
```

**关键**：`*.bilivideo.com` / `*.hdslb.com` 等 CDN 资源**缺少 `Referer` 会返回 403**。因此直接用 `<audio src="...">` 或 `<img src="...">` 引用 `playurl` / `pic` 返回的地址时，必须确保请求带上了 `Referer`：浏览器环境依赖 `Referrer-Policy`（`no-referrer` 会导致加载失败），非浏览器环境需显式设置请求头。

少数接口对 `Referer` 有更具体要求，见各接口详情。

---

## 2. 认证体系

### 2.1 Cookie 清单

**登录凭据（决定"你是谁"）**

| Cookie | 作用 |
| --- | --- |
| `SESSDATA` | 登录凭据，配合 `credentials:'include'` 由浏览器自动携带 |
| `bili_jct` | CSRF token，写操作（点赞/收藏等 POST）需作为 `csrf` 参数提交 |
| `DedeUserID` | 当前用户 `mid` |
| `DedeUserID__ckMd5` | 上述 `mid` 的校验值，登出时一并清除 |

三者（`SESSDATA` + `bili_jct` + `DedeUserID`）齐备即可判定为已登录。

**设备指纹 Cookie（决定"你是不是一个被认可的设备"）**

| Cookie | 作用 |
| --- | --- |
| `buvid3` / `buvid4` | 设备标识。由 B 站前端通过 `GET /x/frontend/finger/spi` 获取后写入 |
| `bili_ticket` | 风控票据，同样由前端流程产生 |
| 其它 `*BUVID*` / 设备类字段 | 随前端版本变化，不宜硬编码枚举 |

> **两者互相独立，切勿混为一谈。** 未登录的正常浏览器访问 `bilibili.com` **同样会**获得 `buvid3`。因此"缺少登录态"与"缺少设备指纹"是两个不同的缺失，其中**后者对风控的影响往往更直接**——详见 [2.3](#23-风控不是一个登录检查而是一个打分器) 与 [9.5](#95-补充-352-的成因推断与验证方法)。

### 2.2 登录态对接口的影响

| 接口 | 未登录 | 已登录 | 证据 |
| --- | --- | --- | --- |
| `nav` | `code: -101`，但 `data.wbi_img` **仍然有效** | `code: 0`，返回用户信息 | 社区共识（广泛报道，本参考未实测） |
| WBI 搜索接口 | 成功率下降，可能 `-352` / `-412` | 正常返回 | **推断**，未经隔离测试 |
| `space/wbi/arc/search` | `-352` 概率升高 | 正常返回 | 推断（基于"-352 风控"相关记述） |
| `view` | 正常返回 | 正常返回 | 社区共识（公开数据接口，无需登录） |
| `playurl` | 仅低画质/低音质流 | 可获取 192kbps AAC、Hi-Res FLAC、杜比全景声 | 推断（高音质与会员权益相关，机制合理） |
| `x/v3/fav/folder/created/list-all` | `code: -101` | 正常返回 | 推断（收藏夹属账号私有数据） |

> ⚠️ **本表没有任何一项经过本参考的实测验证**（上表"证据"列中"社区共识"≠ 实测，仅表示多方一致、可信度较高而非确证）。
> 尤其 WBI 搜索接口：**未登录但携带完整设备指纹时能否正常返回，本表无法回答**——这两者在实践中总是同时出现，容易被错误归因。验证方法见 [9.5](#95-补充-352-的成因推断与验证方法)。

### 2.3 风控不是一个"登录检查"，而是一个打分器

这是最容易误解的一点，也解释了为什么表现是"**更容易被拦**"而不是"**直接拒绝**"。

风控**不会**问"登录了吗"，而是把多个信号合成一个风险分，超过阈值才拦：

| 信号 | 说明 |
| --- | --- |
| IP 信誉 | 住宅 IP vs 数据中心 IP |
| 设备指纹 | `buvid3` 等是否存在、是否稳定 |
| 会话 | 是否登录、账号年龄与历史 |
| 请求频率 | 单位时间请求数、并发数 |
| 请求头自洽性 | UA / `sec-*` / `Referer` 是否互相矛盾 |
| 访问路径合理性 | 是否具备正常导航逻辑 |
| **可问责性** | **登录使流量"有归属、可追责"；匿名流量换个 IP 就消失** |

最后一行是核心：平台对**匿名一次性流量**的容忍度必须更低，因为违规成本几乎为零；而对已登录流量，平台有封号等手段可追溯。这是通用风控逻辑，不是 B 站独有。

由此推出几个反直觉但重要的结论：

- **未登录 + 指纹完整** → 可能通过（这正是未登录浏览 B 站正常工作的原因）
- **已登录 + 指纹缺失 / 请求头矛盾** → 仍可能被拦
- **已登录 + 高频密集请求** → 照样 `-412`
- **隐私模式 / 全新会话 / 清空 Cookie** → 会**同时**丢掉登录态*和*设备指纹，比"仅仅退出登录"更接近"全新设备"

> 因此**不要把"登录"当作通过与否的充分条件**。它是降低风险分的一个信号，而非开关。
| 收藏夹接口 | `code: -101` | 正常返回 |

> **`nav` 的双重职责**：它既是登录态查询接口，也是 WBI 签名密钥（`img_key` / `sub_key`）的唯一来源。因此取密钥时必须用**裸请求**而非统一的"`code !== 0` 即抛错"封装——未登录时它返回 `-101`，需要从错误响应体里照常读取 `data.wbi_img`。

---

## 3. WBI 签名算法

以下接口必须签名：`/x/web-interface/wbi/search/type`、`/x/space/wbi/arc/search`。
其余接口（`view`、`playurl`、`reply`、榜单、收藏夹）**无需**签名。

### 3.1 算法

```
wts    = 当前 Unix 时间戳（秒，10 位）
mixinKey = 置换(img_key + sub_key)[0:32]
query   = 参数按 key 字典序升序拼接为 k=v&k=v...，其中每个 k/v 先做 URL 编码
w_rid   = md5(query + mixinKey)
最终 query = query + "&wts=" + wts + "&w_rid=" + w_rid
```

### 3.2 置换表

`img_key + sub_key` 拼接后得到 64 字符的原始串，按下表下标重排，取前 32 位得到 `mixinKey`：

```js
const MIXIN_KEY_ENC_TAB = [
  46, 47, 18,  2, 53,  8, 23, 32, 15, 50, 10, 31, 58,  3, 45, 35,
  27, 43,  5, 49, 33,  9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
  37, 48,  7, 16, 24, 55, 40, 61, 26, 17,  0,  1, 60, 51, 30,  4,
  22, 25, 54, 21, 56, 59,  6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
]

function getMixinKey(orig) {
  return MIXIN_KEY_ENC_TAB.map((n) => orig[n]).join('').slice(0, 32)
}
```

### 3.3 密钥获取与缓存

```js
async function getWbiKeys() {
  // 注意：不要复用"code!==0 即抛错"的封装，未登录时此接口返回 -101
  const resp = await fetch('https://api.bilibili.com/x/web-interface/nav', {
    credentials: 'include',
    headers: { Referer: 'https://www.bilibili.com' },
  })
  const json = await resp.json()
  const imgUrl = json?.data?.wbi_img?.img_url || ''
  const subUrl = json?.data?.wbi_img?.sub_url || ''
  // 从 https://i0.hdslb.com/bfs/wbi/xxxxxxxx.png 中提取文件名（去扩展名）
  const imgKey = imgUrl.slice(imgUrl.lastIndexOf('/') + 1).split('.')[0]
  const subKey = subUrl.slice(subUrl.lastIndexOf('/') + 1).split('.')[0]
  return { imgKey, subKey }
}
```

密钥**每日轮换**，建议缓存 6 小时左右。取到后按 3.2 拼接置换即可。

### 3.4 参数编码的三个细节

1. **必须剥除 `!'()*`**：参与签名的值需先 `String(v).replace(/[!'()*]/g, '')` 再做 `encodeURIComponent`。这是 WBI 的固定规则，漏掉会导致签名不匹配。
2. **签名与发送必须逐字节一致**：拼好的 query 字符串要直接附加到 URL 上，**不能**再经过 `URL` / `URLSearchParams` 二次编码，否则编码差异会破坏签名。
3. **`wts` 参与签名**：`wts` 与业务参数一同排序、一同参与 `md5` 计算。

### 3.5 完整签名实现

```js
// ===== 纯 JS MD5（ASCII 输入，hex 输出）=====
function md5(str) {
  const rol = (x, c) => (x << c) | (x >>> (32 - c))
  const add = (x, y) => {
    const x4 = x & 0x40000000, y4 = y & 0x40000000, x8 = x & 0x80000000, y8 = y & 0x80000000
    const r = (x & 0x3fffffff) + (y & 0x3fffffff)
    if (x4 & y4) return r ^ 0x80000000 ^ x8 ^ y8
    if (x4 | y4) return (r & 0x40000000) ? (r ^ 0xc0000000 ^ x8 ^ y8) : (r ^ 0x40000000 ^ x8 ^ y8)
    return r ^ x8 ^ y8
  }
  const F = (x, y, z) => (x & y) | (~x & z)
  const G = (x, y, z) => (x & z) | (y & ~z)
  const H = (x, y, z) => x ^ y ^ z
  const I = (x, y, z) => y ^ (x | ~z)
  const FF = (a, b, c, d, x, s, t) => add(rol(add(a, add(add(F(b, c, d), x), t)), s), b)
  const GG = (a, b, c, d, x, s, t) => add(rol(add(a, add(add(G(b, c, d), x), t)), s), b)
  const HH = (a, b, c, d, x, s, t) => add(rol(add(a, add(add(H(b, c, d), x), t)), s), b)
  const II = (a, b, c, d, x, s, t) => add(rol(add(a, add(add(I(b, c, d), x), t)), s), b)
  const toWords = (s) => {
    const len = s.length
    const nWords = (((len + 8 - ((len + 8) % 64)) / 64) + 1) * 16
    const w = new Array(nWords - 1).fill(0)
    let b = 0
    while (b < len) { w[(b - (b % 4)) / 4] |= (s.charCodeAt(b) & 0xff) << ((b % 4) * 8); b++ }
    w[(b - (b % 4)) / 4] |= 0x80 << ((b % 4) * 8)
    w[nWords - 2] = len << 3
    w[nWords - 1] = len >>> 29
    return w
  }
  const toHex = (n) => {
    let s = ''
    for (let i = 0; i <= 3; i++) { const byte = (n >>> (i * 8)) & 255; s += ('0' + byte.toString(16)).slice(-2) }
    return s
  }
  const x = toWords(str)
  let a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476
  for (let k = 0; k < x.length; k += 16) {
    const AA = a, BB = b, CC = c, DD = d
    a = FF(a, b, c, d, x[k], 7, 0xd76aa478); d = FF(d, a, b, c, x[k + 1], 12, 0xe8c7b756); c = FF(c, d, a, b, x[k + 2], 17, 0x242070db); b = FF(b, c, d, a, x[k + 3], 22, 0xc1bdceee)
    a = FF(a, b, c, d, x[k + 4], 7, 0xf57c0faf); d = FF(d, a, b, c, x[k + 5], 12, 0x4787c62a); c = FF(c, d, a, b, x[k + 6], 17, 0xa8304613); b = FF(b, c, d, a, x[k + 7], 22, 0xfd469501)
    a = FF(a, b, c, d, x[k + 8], 7, 0x698098d8); d = FF(d, a, b, c, x[k + 9], 12, 0x8b44f7af); c = FF(c, d, a, b, x[k + 10], 17, 0xffff5bb1); b = FF(b, c, d, a, x[k + 11], 22, 0x895cd7be)
    a = FF(a, b, c, d, x[k + 12], 7, 0x6b901122); d = FF(d, a, b, c, x[k + 13], 12, 0xfd987193); c = FF(c, d, a, b, x[k + 14], 17, 0xa679438e); b = FF(b, c, d, a, x[k + 15], 22, 0x49b40821)
    a = GG(a, b, c, d, x[k + 1], 5, 0xf61e2562); d = GG(d, a, b, c, x[k + 6], 9, 0xc040b340); c = GG(c, d, a, b, x[k + 11], 14, 0x265e5a51); b = GG(b, c, d, a, x[k], 20, 0xe9b6c7aa)
    a = GG(a, b, c, d, x[k + 5], 5, 0xd62f105d); d = GG(d, a, b, c, x[k + 10], 9, 0x02441453); c = GG(c, d, a, b, x[k + 15], 14, 0xd8a1e681); b = GG(b, c, d, a, x[k + 4], 20, 0xe7d3fbc8)
    a = GG(a, b, c, d, x[k + 9], 5, 0x21e1cde6); d = GG(d, a, b, c, x[k + 14], 9, 0xc33707d6); c = GG(c, d, a, b, x[k + 3], 14, 0xf4d50d87); b = GG(b, c, d, a, x[k + 8], 20, 0x455a14ed)
    a = GG(a, b, c, d, x[k + 13], 5, 0xa9e3e905); d = GG(d, a, b, c, x[k + 2], 9, 0xfcefa3f8); c = GG(c, d, a, b, x[k + 7], 14, 0x676f02d9); b = GG(b, c, d, a, x[k + 12], 20, 0x8d2a4c8a)
    a = HH(a, b, c, d, x[k + 5], 4, 0xfffa3942); d = HH(d, a, b, c, x[k + 8], 11, 0x8771f681); c = HH(c, d, a, b, x[k + 11], 16, 0x6d9d6122); b = HH(b, c, d, a, x[k + 14], 23, 0xfde5380c)
    a = HH(a, b, c, d, x[k + 1], 4, 0xa4beea44); d = HH(d, a, b, c, x[k + 4], 11, 0x4bdecfa9); c = HH(c, d, a, b, x[k + 7], 16, 0xf6bb4b60); b = HH(b, c, d, a, x[k + 10], 23, 0xbebfbc70)
    a = HH(a, b, c, d, x[k + 13], 4, 0x289b7ec6); d = HH(d, a, b, c, x[k], 11, 0xeaa127fa); c = HH(c, d, a, b, x[k + 3], 16, 0xd4ef3085); b = HH(b, c, d, a, x[k + 6], 23, 0x04881d05)
    a = HH(a, b, c, d, x[k + 9], 4, 0xd9d4d039); d = HH(d, a, b, c, x[k + 12], 11, 0xe6db99e5); c = HH(c, d, a, b, x[k + 15], 16, 0x1fa27cf8); b = HH(b, c, d, a, x[k + 2], 23, 0xc4ac5665)
    a = II(a, b, c, d, x[k], 6, 0xf4292244); d = II(d, a, b, c, x[k + 7], 10, 0x432aff97); c = II(c, d, a, b, x[k + 14], 15, 0xab9423a7); b = II(b, c, d, a, x[k + 5], 21, 0xfc93a039)
    a = II(a, b, c, d, x[k + 12], 6, 0x655b59c3); d = II(d, a, b, c, x[k + 3], 10, 0x8f0ccc92); c = II(c, d, a, b, x[k + 10], 15, 0xffeff47d); b = II(b, c, d, a, x[k + 1], 21, 0x85845dd1)
    a = II(a, b, c, d, x[k + 8], 6, 0x6fa87e4f); d = II(d, a, b, c, x[k + 15], 10, 0xfe2ce6e0); c = II(c, d, a, b, x[k + 6], 15, 0xa3014314); b = II(b, c, d, a, x[k + 13], 21, 0x4e0811a1)
    a = II(a, b, c, d, x[k + 4], 6, 0xf7537e82); d = II(d, a, b, c, x[k + 11], 10, 0xbd3af235); c = II(c, d, a, b, x[k + 2], 15, 0x2ad7d2bb); b = II(b, c, d, a, x[k + 9], 21, 0xeb86d391)
    a = add(a, AA); b = add(b, BB); c = add(c, CC); d = add(d, DD)
  }
  return (toHex(a) + toHex(b) + toHex(c) + toHex(d)).toLowerCase()
}

// ===== WBI 签名 =====
const MIXIN_KEY_ENC_TAB = [ /* 见 3.2 */ ]
const getMixinKey = (orig) => MIXIN_KEY_ENC_TAB.map((n) => orig[n]).join('').slice(0, 32)

let cache = null // { imgKey, subKey, ts }
async function getWbiKeys() { /* 见 3.3，含 6 小时缓存 */ }

async function encodeWbi(params) {
  const { imgKey, subKey } = await getWbiKeys()
  const mixinKey = getMixinKey(imgKey + subKey)
  const signParams = { ...params, wts: Math.round(Date.now() / 1000) }
  const query = Object.keys(signParams)
    .sort()
    .map((k) => `${encodeURIComponent(k)}=${encodeURIComponent(String(signParams[k]).replace(/[!'()*]/g, ''))}`)
    .join('&')
  return `${query}&w_rid=${md5(query + mixinKey)}`
}
```

> Node.js 环境可直接用 `crypto.createHash('md5')` 替代上面的手写 MD5（注意 URL 编码规则仍需遵守 3.4）。
> 上面这份 MD5 是纯 JS 实现，**仅支持 ASCII 输入**；`encodeURIComponent` 的输出是 ASCII，故满足签名场景。若用于其他含中文的字符串，需先自行做 UTF-8 字节化。

---

## 4. 接口总览

**列含义**

- **环境要求**：`无需` = 公开数据；`建议浏览器环境` = 需设备指纹/会话上下文以降低风控拒绝概率；`需登录` = 无凭据会直接失败
- **需 WBI**：是否必须附加 [第 3 节](#3-wbi-签名算法) 的 `wts` + `w_rid` 签名

| # | 方法 | 路径 | 用途 | 环境要求 | 需 WBI |
| --- | --- | --- | --- | --- | --- |
| 1 | GET | `/x/web-interface/nav` | 登录态 + WBI 密钥 | 无需 | 否 |
| 2 | GET | `/x/passport-login/web/qrcode/generate` | 生成扫码二维码 | 无需 | 否 |
| 3 | GET | `/x/passport-login/web/qrcode/poll` | 轮询扫码状态 | 无需 | 否 | 代码 |
| 4 | GET | `/x/web-interface/wbi/search/type` | 搜索稿件（`search_type=video`） | 建议浏览器环境 | **是** |
| 5 | GET | `/x/web-interface/wbi/search/type` | 搜索 UP 主（`search_type=bili_user`） | 建议浏览器环境 | **是** |
| 6 | GET | `/x/space/wbi/arc/search` | UP 主投稿列表 | 建议浏览器环境 | **是** |
| 7 | GET | `/x/web-interface/view` | 稿件详情（取 `cid`） | 无需 | 否 |
| 8 | GET | `/x/player/playurl` | 播放地址（按 `bvid`） | 建议登录 | 否 |
| 9 | GET | `/x/player/playurl` | 播放地址（按 `avid`） | 建议登录 | 否 |
| 10 | GET | `/x/v2/reply` | 评论区 | 无需 | 否 |
| 11 | GET | `/x/web-interface/popular` | 综合热门 | 无需 | 否 | 推断 |
| 12 | GET | `/x/web-interface/index/top/rcmd` | 个性化推荐 | 建议登录（否则退化为通用推荐） | 否 |
| 13 | GET | `/x/web-interface/ranking/region` | 分区排行榜 | 无需 | 否 |
| 14 | GET | `/x/centralization/interface/music/comprehensive/web/rank` | 音乐综合热歌榜 | 无需 | 否 |
| 15 | GET | `/x/centralization/interface/new/music` | 音乐新歌速递 | 无需 | 否 |
| 16 | GET | `/x/v3/fav/folder/created/list-all` | 收藏夹列表 | 需登录 | 否 |
| 17 | GET | `<playurl 返回的 baseUrl>` | 音频流本体（CDN 直链） | 无需（但**必须带 `Referer`**） | 否 |

接口 8 与 9 是同一个接口的两种入参形式。接口 4 与 5 是同一路径的两种 `search_type`。

> ⚠️ **本表并非官方契约。** "环境要求"一列中：
> - **接口 1 与 17 有"社区共识"级依据**（多方一致，但本参考未实测）
> - **接口 4/5/6 标的是"建议浏览器环境"而非"需登录"**——它们与登录态的相关性在实践中无法与设备指纹分离，容易误归因
> - **接口 16 标"需登录"的依据是"收藏夹属账号私有数据"的机制推断**，非实测返回 `-101`
>
> 换言之：**不要把本表当作硬性规格使用**。涉及登录态与环境要求的判断，请按 [9.5](#95-补充-352-的成因推断与验证方法) 自行隔离验证。
---

## 5. 接口详情

### 5.1 `GET /x/web-interface/nav` — 登录态与 WBI 密钥

```
GET https://api.bilibili.com/x/web-interface/nav
```

无参数。响应 `data` 常用字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `isLogin` | boolean | 是否已登录 |
| `mid` | number | 用户 ID |
| `uname` | string | 昵称 |
| `face` | string | 头像 URL |
| `vipType` | number | 大会员类型 |
| `vipStatus` | number | 大会员状态 |
| `wbi_img.img_url` | string | WBI `img_key` 来源 |
| `wbi_img.sub_url` | string | WBI `sub_key` 来源 |

**未登录时返回 `code: -101`**，但 `data.wbi_img` 依然可用——这是本接口最重要的行为特征。

---

### 5.2 扫码登录（详情见 [第 7 节](#7-扫码登录流程)）

```
GET https://passport.bilibili.com/x/passport-login/web/qrcode/generate
GET https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={key}
```

两个接口的 `Referer` 应为 `https://passport.bilibili.com/login`。

---

### 5.3 `GET /x/web-interface/wbi/search/type` — 搜索

```
GET https://api.bilibili.com/x/web-interface/wbi/search/type?{WBI 签名后的 query}
```

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `search_type` | 是 | `video` 搜稿件；`bili_user` 搜 UP 主 |
| `keyword` | 是 | 关键词 |
| `page` | 否 | 页码，从 1 开始 |
| `page_size` | 否 | 每页条数，常用 20（**注意是 `page_size`，不是旧版的 `pagesize`**） |
| `wts` | 是 | 由签名逻辑附加 |
| `w_rid` | 是 | 由签名逻辑附加 |

`Referer: https://www.bilibili.com`

**`search_type=video` 响应 `data`**：

| 字段 | 说明 |
| --- | --- |
| `seid` | 搜索会话 ID |
| `page` / `pagesize` | 当前页 / 每页条数 |
| `numResults` | 结果总数 |
| `numPages` | 总页数 |
| `result[]` | 稿件数组 |

`result[]` 元素字段：

| 字段 | 说明 |
| --- | --- |
| `bvid` | 稿件 BV 号（播放主键） |
| `aid` | 稿件 AV 号（数字 ID，评论区要用它当 `oid`） |
| `title` | 标题，**含 `<em class="keyword">` 高亮标签，需剥离 HTML** |
| `author` | UP 主昵称 |
| `play` | 播放量 |
| `video_review` | 弹幕数 |
| `duration` | 时长字符串，格式 `"mm:ss"` 或 `"hh:mm:ss"` |
| `pubdate` | 发布时间戳（秒） |
| `pic` | 封面 URL，常为 `//` 协议相对或 `http://` |
| `tag` | 标签串 |
| `description` | 简介 |

**`search_type=bili_user` 响应 `data`**：

| 字段 | 说明 |
| --- | --- |
| `numResults` / `numPages` | 同上 |
| `result[]` | UP 主数组 |

`result[]` 元素字段：`type`、`mid`、`uname`、`usign`（签名）、`fans`（粉丝数）、`videos`（投稿数）、`upic`（头像）、`level`、`is_live`、`official_verify?: { type, desc }`。

---

### 5.4 `GET /x/space/wbi/arc/search` — UP 主投稿列表

```
GET https://api.bilibili.com/x/space/wbi/arc/search?{WBI 签名后的 query}
```

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `mid` | 是 | UP 主 ID |
| `pn` | 否 | 页码，从 1 开始 |
| `ps` | 否 | 每页条数，常用 30 |
| `order` | 否 | `pubdate`（按发布）/ `click`（按播放） |
| `platform` | 否 | 固定传 `web` |
| `wts` / `w_rid` | 是 | 签名附加 |

`Referer` 应为 `https://space.bilibili.com/{mid}/video`。

**响应 `data`**：

| 字段 | 说明 |
| --- | --- |
| `list.vlist[]` | 稿件数组 |
| `page.pn` / `page.ps` / `page.count` | 当前页 / 每页 / 总数 |

`list.vlist[]` 元素字段：`bvid`、`aid`、`title`（含 HTML 标签）、`pic`、`length`（`"mm:ss"` 字符串）、`play`、`created`（时间戳）、`description`、`author`。

> **风控提示**：该接口 `code: -352` 风控概率较高。有效缓解手段是**在真实浏览器环境中发起请求**（完整 Chromium 指纹 + 已登录 Cookie）；从服务端或简化 HTTP 客户端直连时命中率明显更高。

---

### 5.5 `GET /x/web-interface/view` — 稿件详情

```
GET https://api.bilibili.com/x/web-interface/view?bvid={bvid}
```

也可用 `?aid={aid}` 查询。

**响应 `data`**：

| 字段 | 说明 |
| --- | --- |
| `bvid` / `aid` | 稿件 ID |
| `title` | 标题 |
| `desc` | 简介 |
| `pic` | 封面 |
| `owner.mid` / `owner.name` / `owner.face` | UP 主信息 |
| `stat.view` / `.danmaku` / `.like` / `.coin` / `.favorite` / `.share` | 互动数据 |
| `duration` | 总时长（秒） |
| `cid` | **默认分 P 的 cid，取音频流必须用到** |
| `videos` | 分 P 数量 |
| `pages[]` | 分 P 列表，元素含 `cid` / `part` / `duration` |
| `pubdate` | 发布时间戳 |

> **多 P 稿件**：`playurl` 需要 `cid`，若要播放第 N 个分 P，应取 `pages[N-1].cid`，而不是顶层 `cid`。
> **`-404` 情况**：部分「音乐元数据」类 id 是伪 `bvid`，此接口必返 `-404`，但其 `avid + cid` 仍可直取音频流（见 [6.3](#63-回退路径用-avid-与-cid-直取音频流)）。

---

### 5.6 `GET /x/player/playurl` — 播放地址

```
GET https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&qn=0&fnver=0&fnval=16&fourk=1
GET https://api.bilibili.com/x/player/playurl?avid={aid}&cid={cid}&qn=0&fnver=0&fnval=16&fourk=1
```

| 参数 | 说明 |
| --- | --- |
| `bvid` 或 `avid` | 二选一，稿件标识 |
| `cid` | **必填**，来自 `view` 接口 |
| `qn` | 期望画质编号；传 `0` 表示"不指定"，由服务端返回可用最高 |
| `fnver` | 固定 `0` |
| `fnval` | **位掩码，传 `16` 表示请求 DASH 格式**——只有 DASH 才会返回独立音频流，这是提取音频的关键 |
| `fourk` | 传 `1` 允许 4K |

**响应 `data`**：

| 字段 | 说明 |
| --- | --- |
| `quality` | 实际返回的画质编号 |
| `format` | 容器格式 |
| `dash.audio[]` | **音频流数组（提取目标）** |
| `dash.video[]` | 视频流数组 |

`dash.audio[]` / `dash.video[]` 元素字段：

| 字段 | 说明 |
| --- | --- |
| `id` | 流编号 = 音质/画质编号（见 [6.2](#62-音质编号对照表)） |
| `quality` | 同上，语义化编号 |
| `bandwidth` | 码率（bps），**选流排序依据** |
| `mimeType` | MIME 类型，如 `audio/mp4` |
| `codecid` | 编码格式编号 |
| `baseUrl` | 主播放地址 |
| `backupUrl[]` | 备用地址数组 |
| `width` / `height` | 仅视频流有 |

`baseUrl` **会过期**（带有时效性签名参数），不应长期缓存，每次播放前重新请求。

---

### 5.7 `GET /x/v2/reply` — 评论区

```
GET https://api.bilibili.com/x/v2/reply?type=1&oid={aid}&pn=1&ps=20&sort=2
```

| 参数 | 说明 |
| --- | --- |
| `type` | 评论区类型，稿件固定 `1` |
| `oid` | **内容 ID —— 稿件请传数字 `aid`，不是 `bvid`** |
| `pn` | 页码 |
| `ps` | 每页条数 |
| `sort` | 排序：`0` 按时间、`1` 按点赞、`2` 按热度 |

**响应 `data`**：

| 字段 | 说明 |
| --- | --- |
| `page.num` / `.size` / `.count` / `.acount` | 当前页 / 每页 / 评论总数 / 回复总数 |
| `replies` | 评论数组，**可能为 `null`（无评论/已关闭）** |

`replies[]` 元素字段：`rpid`（评论 ID）、`ctime`（时间戳）、`like`、`rcount`（回复数）、`member.mid` / `.uname` / `.avatar`、`content.message`。

---

### 5.8 `GET /x/web-interface/popular` — 综合热门

```
GET https://api.bilibili.com/x/web-interface/popular?ps=10&pn=1
```

| 参数 | 说明 |
| --- | --- |
| `ps` | 每页条数 |
| `pn` | 页码 |

**响应 `data.list[]`**：元素含 `bvid`、`aid`、`title`、`pic`、`owner{mid,name,face}`、`stat{...}`、`duration`。

---

### 5.9 `GET /x/web-interface/index/top/rcmd` — 个性化推荐

```
GET https://api.bilibili.com/x/web-interface/index/top/rcmd?ps=20
```

**响应 `data.item[]`**：元素含 `bvid`、`id`（即 `aid`）、`title`、`desc`、`pic`、`owner{name,mid}`、`duration`、`cid`、`stat{view,like,favorite}`。

> 该接口的推荐结果**强依赖登录 Cookie**；未登录时返回的是通用推荐列表。
> *（证据：推断。机制合理且与"未登录退化为通用推荐"的普遍行为一致，但未做隔离验证。）*

---

### 5.10 `GET /x/web-interface/ranking/region` — 分区排行榜

```
GET https://api.bilibili.com/x/web-interface/ranking/region?rid=3&day=3
```

| 参数 | 说明 |
| --- | --- |
| `rid` | 分区 ID —— `3` = 音乐区 |
| `day` | 统计周期：`1` 日榜、`3` 三日榜、`7` 周榜 |

**响应 `data`**：注意该接口的 `data` **可能直接是数组**，也可能被包在 `list` 或 `data` 字段里。稳健的取值方式：

```js
const list = Array.isArray(data) ? data : (data.list || data.data || [])
```

元素字段：`bvid`、`aid`、`title`、`pic`、`owner`、`stat`、`duration`、`cid`、`pubdate`。

---

### 5.11 音乐中心：综合热歌榜 / 新歌速递

对应 `music.bilibili.com/pc/music-center` 页面使用的同源数据，**无需 WBI、无需 csrf**。

```
GET https://api.bilibili.com/x/centralization/interface/music/comprehensive/web/rank?pn=1&ps=30
GET https://api.bilibili.com/x/centralization/interface/new/music
```

两者响应 `data.list[]`，元素字段一致：

| 字段 | 说明 |
| --- | --- |
| `music_id` | 音乐 ID |
| `music_title` | 曲名 |
| `author` | 歌手/UP 主 |
| `bvid` / `aid` / `cid` | 稿件标识 |
| `cover` | 封面 |
| `album` | 专辑 |
| `score` / `total_vv` | 热度分 / 播放量（综合榜有） |
| `publish_time` | 发布时间（新歌有） |
| `related_archive` | 关联的可播放稿件对象 |

> **关键差异（务必区分）**：
> - **综合热歌榜**：顶层 `bvid` 是 `music-metadata` 伪 id，直接拿去调 `view`/`playurl` 会 `-404`。**实际可播放稿件在 `related_archive.bvid`**（或退而用顶层 `aid` + `cid`）。
> - **新歌速递**：无 `related_archive`，顶层 `bvid` 即为可播放稿件。
>
> 通用的可播 id 解析逻辑：
> ```js
> const playableBvid = (x) => x.related_archive?.bvid || x.bvid
> ```
> 注意 `related_archive.cid` 不可靠，回退时应使用**顶层 `aid` + 顶层 `cid`**。

---

### 5.12 `GET /x/v3/fav/folder/created/list-all` — 收藏夹列表

```
GET https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid={mid}
```

| 参数 | 说明 |
| --- | --- |
| `up_mid` | 用户 ID；查自己的收藏夹即传自己的 `mid` |

**响应 `data`**：`count`（收藏夹数量）、`list[]`（元素含 `id`、`title`、`media_count`）。

---

### 5.13 音频流本体

`playurl` 返回的 `dash.audio[].baseUrl` 就是可直接播放/下载的音频直链（CDN 域，见第 1 节）。

- **播放**：直接赋给 `<audio src>` 即可；但必须满足 CDN 的 `Referer` 要求，否则 403。
- **下载**：`GET` 该 URL 并带上 `Referer: https://www.bilibili.com`，写入文件即可。

---

## 6. 音频流提取

### 6.1 选流策略

```js
function getBestAudioUrl(playData) {
  const audioStreams = playData.dash?.audio
  if (!audioStreams?.length) throw new Error('No audio stream available')
  // 按 bandwidth 降序取首，即当前账号可获取的最高音质
  return [...audioStreams].sort((a, b) => b.bandwidth - a.bandwidth)[0].baseUrl
}
```

**为什么按 `bandwidth` 排而不是按 `id` 排**：`id` 的数值大小并不严格对应音质高低（`30250` 杜比 / `30251` Hi-Res 的数值与常规 AAC 序列不同源），而 `bandwidth` 是实际码率，跨类型比较更可靠。

### 6.2 音质编号对照表

`dash.audio[].id` 常见取值：

| id | 音质 |
| --- | --- |
| `30216` | 64 kbps MP3 |
| `30232` | 132 kbps AAC |
| `30280` | 192 kbps AAC（高品质） |
| `30250` | 杜比全景声（Dolby Atmos） |
| `30251` | Hi-Res 无损 FLAC |

高音质（`30280` 及以上）通常需要登录，部分需大会员。
*（证据：推断。与"高音质属会员权益"的机制一致，但未逐档实测各音质编号的登录门槛。）*

### 6.3 回退路径：用 avid 与 cid 直取音频流

当 `bvid` 对应的稿件不存在（`view` 返回 `-404`）时，若手上还有 `aid` + `cid`，可以**跳过 `view` 直接调 `playurl`**：

```js
async function extractByAvidCid(aid, cid) {
  const url = `https://api.bilibili.com/x/player/playurl?avid=${aid}&cid=${cid}&qn=0&fnver=0&fnval=16&fourk=1`
  const playData = await fetchBili(url)
  const best = [...playData.dash.audio].sort((a, b) => b.bandwidth - a.bandwidth)[0]
  return { audioUrl: best.baseUrl, quality: best.quality, mimeType: best.mimeType }
}
```

代价是这条路径拿不到 `title` / `owner` / `pic` / `duration`，调用方需自行保留已有的展示信息。

### 6.4 完整提取流程

```
关键词/榜单
   ↓
搜索或榜单接口 ──→ bvid（+ 可能的 aid / cid 回退信息）
   ↓
GET /x/web-interface/view?bvid=…  ──→ cid（多 P 取 pages[n-1].cid）
   ↓                           └── 失败且持有 aid+cid → 走 6.3 回退
GET /x/player/playurl?bvid=…&cid=…&fnval=16  ──→ dash.audio[]
   ↓
按 bandwidth 降序取首 ──→ baseUrl ──→ <audio src>
```

---

## 7. 扫码登录流程

### 7.1 取码

```
GET https://passport.bilibili.com/x/passport-login/web/qrcode/generate
Referer: https://passport.bilibili.com/login
```

响应 `data`：`url`（二维码内容，用二维码库渲染成图）、`qrcode_key`（轮询凭据，**需自行保存**）。

### 7.2 轮询

```
GET https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={key}
Referer: https://passport.bilibili.com/login
```

**响应结构有嵌套陷阱**：

- 外层 `code` = API 调用结果
- 内层 `data.code` = **真正的扫码状态**

务必读取内层：

```js
const status = data.data?.code ?? data.code
```

| `status` | 含义 | 证据 |
| --- | --- | --- |
| `86101` | 未扫码 | 推断（同一常量在公开实现中出现，但**未实测**） |
| `86100` | 未扫码（等同义值） | 推断（有实现使用此值，与 `86101` 并存） |
| `86090` | 已扫码，待用户在手机上确认 | 推断（多来源一致，可信度较高） |
| `0` | **登录成功** | 代码（多来源一致） |
| `86038` | 二维码已失效，需重新取值 | **推断，未实测** |

> ⚠️ **关于"已扫码但拒绝确认"**：是否存在独立的"拒绝"状态码**未知**。
> 若手机上拒绝了确认而服务端不通知，`poll` 会一直返回 `86090` 直到二维码自然失效（`86038`）。
> 因此客户端设计上**不应依赖枚举全部状态**——见 [7.5](#75-轮询节奏与终止条件) 的"未知状态"处理。

登录成功后，响应会带 `Set-Cookie`（`SESSDATA`、`bili_jct`、`DedeUserID` 等）。

### 7.3 Cookie 落地

**浏览器环境**：以 `credentials:'include'` 发起轮询，Cookie 由浏览器自动写入，无需手动处理。

**Node / Electron 环境**：用 `redirect: 'manual'` 发起请求以捕获 302 响应，再从 `Set-Cookie` 中解析并手动持久化：

```js
const resp = await fetch(pollUrl, { redirect: 'manual' })
const setCookies = resp.headers.getSetCookie?.() || []
const isRedirect = resp.status === 301 || resp.status === 302
if (isRedirect && setCookies.length) {
  // 解析 name=value（value 中可能含 '='，需 splice 后 join）
  // 持久化到自己的 Cookie 存储
}
```

### 7.4 登出

清除 `SESSDATA`、`bili_jct`、`DedeUserID`、`DedeUserID__ckMd5` 四个 Cookie 即可。浏览器跨域场景无法直接清除 `bilibili.com` 的 Cookie，需要走 B 站的登出接口或由宿主环境代为清除。

### 7.5 轮询节奏与终止条件

`poll` 是**公共的未认证端点**（无 WBI 签名、无 `appkey`），按契约正常调用即可；但它的调用特征是"短时间内反复请求"，节奏失控会明显偏离正常客户端。建议参数：

| 项 | 建议值 |
| --- | --- |
| 轮询间隔 | **1.5 ~ 2 秒**（不要低于 1 秒） |
| 二维码有效期 | 约 3 分钟 |
| 全程请求次数 | 约 90 ~ 120 次（正常量级） |
| 单次请求超时 | 10 秒左右，超时按一次失败处理而非中断整个流程 |
| 连续失败容忍 | 少量网络抖动应重试而非直接放弃轮询 |

**状态机**：从"未扫码"进入，经"已扫码待确认"到"登录成功"；任一环节都可能转入"已失效"。

> 注意下面的常量**并非全部经过验证**（证据见 [7.2](#72-轮询) 表）。当常量不可信时，下面的"未知值"一行就是唯一的兜底——**它必须存在**。

| `status` | 含义 | 动作 |
| --- | --- | --- |
| `86101` / `86100` | 未扫码 | 继续轮询 |
| `86090` | 已扫码，待确认 | 继续轮询（UI 提示"已在手机确认"） |
| `0` | **登录成功** | **立即停止轮询**，处理 302 与 `Set-Cookie`（见 [7.3](#73-cookie-落地)） |
| `86038` | 二维码已失效 | **立即停止轮询**，提示用户重新取值 |
| **其它任意值** | **未预期/未确认状态** | **记录日志 + 继续轮询**，由兜底超时收口。**不要静默丢弃，也不要当作失败终止** |

> "其它任意值"按此处理的原因：**状态集合未知**（"已扫码但拒绝确认"是否有独立码未经确认）。若把它当作失败终止，可能把可恢复状态误判为失败；若静默忽略，则丢失排查线索。**继续轮询 + 日志 + 超时兜底**是唯一安全的选择。

**必须保证终止的四种出口**，缺一不可：

1. `status === 0` —— 成功
2. `status === 86038` —— 失效
3. **用户主动取消 / 关闭登录界面** —— 通过 `CancellationToken` 一类机制通知轮询循环退出
4. **达到有效期上限**（约 3 分钟）的兜底超时 —— 即使一次 `86038` 都没收到，也必须退出

第 3 条最容易被漏掉：轮询循环若挂在无法取消的任务里，用户关掉界面后它仍会继续请求到有效期结束。

参考实现（终止条件显式化）：

```js
async function pollUntilDone(key, { intervalMs = 1800, maxMs = 180000, signal } = {}) {
  const deadline = Date.now() + maxMs

  while (true) {
    if (signal?.aborted) return { ok: false, reason: 'cancelled' }   // 出口 3
    if (Date.now() > deadline) return { ok: false, reason: 'timeout' } // 出口 4

    let status
    try {
      const resp = await fetchPoll(key, { signal })
      if (resp.httpStatus === 301 || resp.httpStatus === 302) {
        return { ok: true, cookies: resp.setCookies }                // 出口 1
      }
      status = resp.body?.data?.code ?? resp.body?.code
    } catch (e) {
      if (signal?.aborted) return { ok: false, reason: 'cancelled' }
      await sleep(intervalMs, signal)   // 网络抖动：按一次失败处理，继续轮询
      continue
    }

    if (status === 0) return { ok: true }                            // 出口 1
    if (status === 86038) return { ok: false, reason: 'expired' }     // 出口 2

    await sleep(intervalMs, signal)
  }
}

const sleep = (ms, signal) => new Promise((resolve) => {
  const onAbort = () => { clearTimeout(t); resolve() }
  const t = setTimeout(() => {
    signal?.removeEventListener('abort', onAbort)  // 正常超时后摘掉监听，避免泄漏
    resolve()
  }, ms)
  signal?.addEventListener('abort', onAbort, { once: true })
})
```

> **不建议把间隔压到 1 秒以下**：`poll` 本身不取内容数据，但没有必要用高频请求换取用户几乎感知不到的一两秒差异，而这个请求特征本身是可避免的。
>
> `qrcode_key` 与二维码一一对应，同一 `key` 不要并发轮询。

### 7.6 会话持久化与安全

登录成功后 `Set-Cookie` 中的 `SESSDATA` **等价于账号登录态**（配合 `bili_jct` 可执行写操作）。持久化策略属于安全责任，请勿省略。

**1. 存储必须加密**

不要把 `SESSDATA` 以明文写入配置文件、JSON、注册表或日志。按平台选择系统提供的凭据保护机制：

| 平台 | 推荐机制 |
| --- | --- |
| Windows | DPAPI（`ProtectedData`）、Windows Credential Manager |
| macOS | Keychain |
| Linux | Secret Service / libsecret（无桌面环境时退化为用户态受限权限文件） |
| 浏览器扩展 | `chrome.storage.session` 或宿主页面的加密存储 |

**2. 读写与传输的最小暴露**

- 仅在与 B 站域名通信时才附加该 Cookie，避免随其它请求外发
- 不要把 Cookie 拼进 URL（会进日志、进 Referer、进浏览器历史）
- 不要在日志、崩溃报告、遥测中输出其值（如需排查，只输出长度或哈希前缀）
- 撤销/过期后立即从存储中删除，不要留作"备用"

**3. 过期检测**

`nav` 返回 **`code: -101`** 即表示会话已失效（注意 `nav` 是特例：不能复用"`code !== 0` 即抛错"的统一封装，见 [8.3](#83-通用封装) 的说明）。

- 主动检测：启动时调一次 `nav`，或定时低频校验
- 被动检测：任意接口返回 `-101` 时，立即把登录态标记为"已过期"
- **失效后必须给出明确状态**，不能让 UI 继续显示已登录而接口一路返回 `-101`

**4. 登出**

清除 4 个 Cookie：`SESSDATA`、`bili_jct`、`DedeUserID`、`DedeUserID__ckMd5`，并同步清空持久化存储（见 [7.4](#74-登出)）。

**5. 多端与异地同时登录**

账号同时登录桌面端与手机 App，可能触发平台的二次验证要求。这属于**账号行为层面**的事项，与客户端技术栈无关；程序侧能做的是在登录失败/需验证时给出清晰提示，而不是静默重试。

> **补充：登录之后才是风控的主要关注区**。`generate` / `poll` 是认证端点，不取内容数据；真正容易触发限制的是登录后对内容接口的调用模式。因此不建议在登录成功后立刻批量"预热"（一次性拉取榜单、关注列表、播放历史等）——应让请求由用户操作驱动，并对所有内容接口统一加节流，对 `-412` 立即退避。
>
> 另注：不要以为"清空 Cookie"就等于"回到未登录状态"。它会**同时**抹掉设备指纹（`buvid3` 等），反而更接近"全新陌生设备"，风控敏感度更高——见 [2.3](#23-风控不是一个登录检查而是一个打分器) 与 [9.5](#95-补充-352-的成因推断与验证方法)。

---

## 8. 统一约定与错误处理

### 8.1 响应结构

所有接口统一返回：

```json
{ "code": 0, "message": "0", "ttl": 1, "data": { } }
```

**`code === 0` 为成功**，此时 `data` 才是有效载荷。

### 8.2 常用错误码

| code | 含义 | 处理建议 | 证据 |
| --- | --- | --- | --- |
| `0` | 成功 | — | 代码观察 |
| `-101` | 账号未登录 | 引导登录；`nav` 例外，其 `wbi_img` 仍可用 | 代码观察 |
| `-352` | 风控校验失败 | 先按 [9.5](#95-补充-352-的成因推断与验证方法) 分离变量定位成因，再决定是否换环境 | 代码观察（`-352` 本身）/ 成因**推断** |
| `-404` | 稿件不存在 | 若是音乐元数据伪 id，走 `aid`+`cid` 回退（[6.3](#63-回退路径用-avid-与-cid-直取音频流)） | 代码观察 |
| `-412` | 请求被拦截（频率过高 / 网络环境异常） | 降速、退避重试 | 推断 |
| `-403` | 权限不足 | 通常需登录或大会员 | 推断 |

> 特例：**扫码轮询接口的 `code` 语义不同**，见 [7.2](#72-轮询)。
>
> ⚠️ **所有错误响应都可能带 HTTP 200**。务必以业务字段 `code` 判定成败，不要依赖 HTTP 状态码或 `resp.ok` —— 详见 [11](#11-已知坑位清单) 第 23 条。

### 8.3 通用封装

```js
async function fetchBili(path, params = {}) {
  const url = new URL('https://api.bilibili.com' + path)
  Object.entries(params).forEach(([k, v]) => url.searchParams.set(k, String(v)))

  const resp = await fetch(url.toString(), {
    credentials: 'include',                       // 携带 Cookie
    headers: { Referer: 'https://www.bilibili.com' },
  })
  const data = await resp.json()
  if (data.code !== 0) {
    throw Object.assign(new Error(`Bilibili API [${data.code}]: ${data.message}`), {
      code: data.code,
      path,
    })
  }
  return data.data
}
```

> ⚠️ 该封装**不适用于**：`nav`（需容忍 `-101`）与扫码轮询（`code` 语义不同）。这两处需单独处理。

### 8.4 数据清洗约定

| 问题 | 处理 |
| --- | --- |
| 封面/头像 URL 为 `//` 协议相对或 `http://` | 统一升为 `https://`。混合内容（cleartext）在现代浏览器/Electron 渲染进程中会加载失败 |
| 搜索/投稿标题含 `<em class="keyword">` 等 HTML 标签 | `title.replace(/<[^>]+>/g, '')` |
| `duration`（搜索）与 `length`（投稿）是 `"mm:ss"` 字符串 | 解析为秒；兼容 `"hh:mm:ss"` |
| `duration`（详情/榜单）已是秒数 | 直接用 |
| `replies` 可能为 `null` | 兜底为 `[]` |

时长解析参考实现：

```js
function parseLength(len) {
  if (!len) return 0
  const parts = len.split(':').map(Number)
  if (parts.length === 2) return parts[0] * 60 + parts[1]
  if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2]
  return 0
}
```

---

## 9. 跨域与反爬

B 站接口**不返回允许第三方站点的 CORS 头**，浏览器直接 `fetch` 会被同源策略拦截。可选方案：

### 方案 A：Electron / 内嵌 Chromium（推荐，反爬克制最好）

在 Electron 中通过 `session.webRequest` 同时处理请求头与响应头。

**前提认知**：同一 session 的 `webRequest` 监听器**每类事件只能注册一个**，后注册者会覆盖先注册者。因此「补 `Referer`」与「注入 CORS 头」必须合并在同一对监听器里，不能分两处注册。

```js
const biliUrls = [
  'https://*.hdslb.com/*',
  'https://*.bilivideo.com/*',
  'https://*.bilivideo.cn/*',
  'https://*.akamaized.net/*',
  'https://*.bilibili.com/*',
]

// 1) 出站：强制补 Referer / Origin（CDN 缺 Referer 会 403）
const reqOrigin = new Map()
session.defaultSession.webRequest.onBeforeSendHeaders({ urls: biliUrls }, (details, callback) => {
  const headers = details.requestHeaders
  const origin = headers['Origin'] || headers['origin']
  if (origin && /^(https?|app):\/\//.test(origin) && /\/\/(api|passport)\.bilibili\.com/.test(details.url)) {
    reqOrigin.set(details.id, origin)   // 记下真实 origin，供回显
  }
  headers['Referer'] = 'https://www.bilibili.com'
  headers['Origin'] = 'https://www.bilibili.com'
  callback({ requestHeaders: headers })
})

// 2) 入站：给 API 响应重写 CORS 头
const apiUrls = { urls: ['https://api.bilibili.com/*', 'https://passport.bilibili.com/*'] }
session.defaultSession.webRequest.onHeadersReceived(apiUrls, (details, callback) => {
  const respHeaders = {}
  for (const [key, value] of Object.entries(details.responseHeaders || {})) {
    if (!key.toLowerCase().startsWith('access-control-allow')) respHeaders[key] = value
  }
  const origin = reqOrigin.get(details.id)
  reqOrigin.delete(details.id)
  // credentials:'include' 下 ACAO 必须是具体 origin，不能是 '*'
  respHeaders['access-control-allow-origin'] = [origin || '*']
  respHeaders['access-control-allow-credentials'] = ['true']
  respHeaders['access-control-allow-methods'] = ['GET, POST, PUT, DELETE, OPTIONS']
  respHeaders['access-control-allow-headers'] = ['*']
  callback({ responseHeaders: respHeaders })
})
```

**注意点**：带 `credentials:'include'` 的跨域请求，`Access-Control-Allow-Origin` **不允许为 `*`**，必须回显请求的真实 `Origin`。开发环境（如 `http://localhost:5173`）会因此被拦，而 `file://` 等 origin 为 `null` 的场景可退回 `*`。

### 方案 B：服务端代理

在自有后端转发请求，由后端负责补 `Referer` 并携带 Cookie，前端只与自己的后端通信，无 CORS 问题。适合需要集中管理 Cookie 或做缓存/限流的场景。

### 方案 C：浏览器扩展

通过扩展的 `declarativeNetRequest` / `webRequest` 权限改写请求头并绕过同源限制。

### 反爬注意事项

| 现象 | 原因 | 缓解 |
| --- | --- | --- |
| `-352` | **成因见 [9.5](#95-补充-352-的成因推断与验证方法)**，可能为设备指纹缺失或请求特征异常 | 在真实 Chromium 环境发起；补齐 `Referer`/`Origin`/`UA` |
| CDN 403 | 音频/图片请求缺 `Referer` | 确保所有 CDN 请求带上 `Referer` |
| `-412` | 请求频率过高 / 网络环境异常 | 加节流与退避重试；检查设备指纹是否稳定 |
| 签名失败 | 编码规则不符 | 严格遵循 [3.4](#34-参数编码的三个细节) |
| `baseUrl` 失效 | 播放地址有时效签名 | 不长期缓存，播放前重新获取 |

### 9.5 补充：`-352` 的成因推断与验证方法

**首先要认清一个事实**：`-352` 的**准确成因无法从公开信息确定**。它可能来自：

- 缺少设备指纹（`buvid3` / `bili_ticket` 等）
- 缺少登录态
- 请求头不自洽（如 UA 自称浏览器但前后矛盾）
- TLS / HTTP2 指纹与声称的浏览器不符
- 请求频率或访问路径异常

**这些因素在实践中总是同时出现或同时缺失**，所以很容易被错误归因。本节给出可**分离变量**的验证方法。

#### 常见错误归因

| 错误结论 | 为什么错 |
| --- | --- |
| "必须登录才能搜索" | 未登录的正常浏览器**也能**搜索——它只是同时带了设备指纹 |
| "退出登录后就被风控了" | 更可能是**隐私模式 / 全新会话**导致设备指纹一并丢失 |
| "主进程发请求一定被拦" | 需先排除 UA 自曝（如 `Electron/x.y.z`、`python-requests/x` 之类）这一更廉价的解释 |

#### 分离变量的验证方案

把两个变量分开控制，一次测试即可定位：

| 测试 | 保留 | 去掉 | 通过 → | 失败 → |
| --- | --- | --- | --- | --- |
| **A** | 全部 Cookie（**含 `buvid3`**） | 仅 `SESSDATA` | **无需登录**，有指纹即可 | 登录态是必需项 |
| **B** | 仅 `SESSDATA` | `buvid3` / `bili_ticket` 等 | 指纹非必需 | **指纹是必需项** |

**关键**：测试 A **必须保留 `buvid3`**。若用"清空 Cookie"或"全新无痕窗口"来做，会同时抹掉登录态*和*设备指纹，**无法区分成因**——这是最常见的实验设计错误。

#### 判定依据：看 `code`，不是看 HTTP 状态

```
HTTP 200
{ "code": -352, "message": "风控校验失败", "data": null }
```

| 返回 | 结论 |
| --- | --- |
| `code: 0` + 结果 | 该变量组合可通过 |
| `code: -101` | 硬性要求登录 |
| `code: -352` / `-412` | 风控层拒绝（软失败，可通过改善环境缓解） |
| `code: 0` 但结果降级/截断 | 部分可用 |

#### 若怀疑是"廉价原因"，先排除它

按成本从低到高排查，**不要一上来就换环境**：

1. **UA 自曝**：确认请求头里没有 `Electron/`、`okhttp`、`axios`、`python-*` 等特征串（`UA` 应为一个完整的现代浏览器串）
2. **请求头不一致**：`UA` 声称 Chrome，但 `Accept`/`Accept-Language`/`sec-ch-ua` 缺失或互相矛盾——**一致性比完整性更重要**
3. **设备指纹**：确认 `buvid3` 存在且稳定（不要在请求之间变换）
4. **频率**：确认没有并发尖峰
5. **最后**才是环境升级（内嵌 Chromium / 服务端代理）

> 第 1、2 项属于"廉价原因"，却在实践中占比不低。**在换架构之前，先把这两项确认掉。**

---

## 10. 最小可用示例

### 10.1 搜索并取第一首的音频地址（Node 18+ / 现代浏览器）

```js
const API = 'https://api.bilibili.com'
const HEADERS = { Referer: 'https://www.bilibili.com' }

// —— 依赖第 3 节的 md5 / MIXIN_KEY_ENC_TAB / getMixinKey / getWbiKeys / encodeWbi ——

async function searchVideo(keyword, page = 1, pageSize = 20) {
  const query = await encodeWbi({
    search_type: 'video', keyword, page, page_size: pageSize,
  })
  // 注意：直接把签名好的 query 拼上去，不要再用 URL 二次编码
  const resp = await fetch(`${API}/x/web-interface/wbi/search/type?${query}`, {
    credentials: 'include', headers: HEADERS,
  })
  const data = await resp.json()
  if (data.code !== 0) throw new Error(`[${data.code}] ${data.message}`)
  return data.data
}

async function getCid(bvid) {
  const resp = await fetch(`${API}/x/web-interface/view?bvid=${bvid}`, {
    credentials: 'include', headers: HEADERS,
  })
  const data = await resp.json()
  if (data.code !== 0) throw new Error(`[${data.code}] ${data.message}`)
  return data.data.cid
}

async function getBestAudio(bvid, cid) {
  const params = new URLSearchParams({
    bvid, cid: String(cid), qn: '0', fnver: '0', fnval: '16', fourk: '1',
  })
  const resp = await fetch(`${API}/x/player/playurl?${params}`, {
    credentials: 'include', headers: HEADERS,
  })
  const data = await resp.json()
  if (data.code !== 0) throw new Error(`[${data.code}] ${data.message}`)
  const streams = data.data?.dash?.audio || []
  if (!streams.length) throw new Error('No audio stream available')
  const best = [...streams].sort((a, b) => b.bandwidth - a.bandwidth)[0]
  return { url: best.baseUrl, quality: best.quality, mimeType: best.mimeType }
}

// —— 串联 ——
async function playFirstResult(keyword) {
  const search = await searchVideo(keyword)
  const first = search.result?.[0]
  if (!first) throw new Error(`No result for: ${keyword}`)

  const cid = await getCid(first.bvid)
  const audio = await getBestAudio(first.bvid, cid)

  const el = new Audio()
  el.src = audio.url
  await el.play()
  return { title: first.title.replace(/<[^>]+>/g, ''), ...audio }
}
```

> 浏览器环境中，`<audio src>` 指向 `bilivideo` 域时是否携带 `Referer` 取决于页面的 `Referrer-Policy`。若设为 `no-referrer` 会得到 403，需放宽策略或改用代理。

### 10.2 扫码登录（拿到二维码）

```js
const PASSPORT = 'https://passport.bilibili.com'
const LOGIN_HEADERS = { Referer: 'https://passport.bilibili.com/login' }

async function createQrSession() {
  const resp = await fetch(`${PASSPORT}/x/passport-login/web/qrcode/generate`, {
    credentials: 'include', headers: LOGIN_HEADERS,
  })
  const { data } = await resp.json()
  return { qrContent: data.url, key: data.qrcode_key }
}

async function pollQr(key) {
  const resp = await fetch(
    `${PASSPORT}/x/passport-login/web/qrcode/poll?qrcode_key=${key}`,
    { credentials: 'include', headers: LOGIN_HEADERS },
  )
  const data = await resp.json()
  // 内层 data.code 才是扫码状态
  const status = data.data?.code ?? data.code
  const map = { 86101: '待扫码', 86090: '待确认', 0: '登录成功', 86038: '已失效' }
  return { status, label: map[status] ?? `未知(${status})`, raw: data }
}

// 使用：轮询直到 status === 0
```

---

## 11. 已知坑位清单

按"踩中概率 × 排查耗时"排序，均为实际会遇到的问题。

| # | 坑位 | 正确做法 |
| --- | --- | --- |
| 1 | 用 `search/type` 老路径 + `pagesize` 参数 | WBI 版路径是 `/x/web-interface/wbi/search/type`，参数名是 `page_size` |
| 2 | 签名后把 query 塞进 `URL` / `URLSearchParams` | 会造成二次编码破坏签名，必须原样拼接 |
| 3 | 签名时忘记剥除 `!'()*` | 参与签名的值先 `replace(/[!'()*]/g,'')` 再编码 |
| 4 | 用统一封装请求 `nav`，未登录时抛错导致拿不到 WBI 密钥 | `nav` 需容忍 `-101`，从错误响应里照常取 `data.wbi_img` |
| 5 | 扫码轮询读外层 `code` | 真正的状态在内层 `data.code` |
| 6 | 用 `bvid` 当评论 `oid` | `oid` 必须是数字 `aid` |
| 7 | 忘记传 `fnval=16` | 不加这项拿不到 DASH 独立音频流 |
| 8 | 直接缓存 `baseUrl` 长期复用 | 地址带时效签名，播放前重新获取 |
| 9 | 多 P 稿件只取顶层 `cid` | 顶层是默认分 P，其他分 P 取 `pages[n-1].cid` |
| 10 | 音乐综合榜把顶层 `bvid` 直接喂给 `view` | 顶层是伪 id，应取 `related_archive.bvid`，或退用顶层 `aid`+`cid` |
| 11 | 回退时用 `related_archive.cid` | 该字段不可靠，回退要用**顶层** `aid` + **顶层** `cid` |
| 12 | 榜单接口按固定结构解析 `data` | `ranking/region` 的 `data` 可能是数组、`list` 或 `data`，需三者兼容 |
| 13 | 直接 `<audio src>` 但漏 `Referer` | CDN 缺 `Referer` 返 403；注意 `Referrer-Policy: no-referrer` 的副作用 |
| 14 | 封面 `http://` / `//` 直接使用 | 统一升 `https://`，否则混合内容被浏览器拦截 |
| 15 | 标题中的 `<em>` 标签直接展示 | 搜索结果标题含 HTML 高亮标签，需剥离 |
| 16 | 分开注册两个 `webRequest` 监听器 | 同类事件只允许一个监听器，后注册者覆盖前者，必须合并注册 |
| 17 | `credentials:'include'` 下 CORS 回 `*` | 必须回显真实 `Origin` |
| 18 | 假定 `replies` 一定是数组 | 无评论时为 `null` |
| 19 | 按 `id` 数值大小选音质 | 应按 `bandwidth` 降序选 |
| 20 | 高频请求搜索/空间接口 | 触发 `-412`/`-352`，需节流退避 |
| 21 | 扫码轮询无终止条件：只写 `while(true)`，不处理失效、取消、兜底超时 | 必须显式实现四种退出（见 [7.5](#75-轮询节奏与终止条件)）；漏掉取消会让轮询在用户关闭界面后继续空转 |
| 22 | `SESSDATA` 明文落盘 | 它等价于账号登录态，必须用系统凭据保护机制加密存储（见 [7.6](#76-会话持久化与安全)） |
| 23 | 用 HTTP 状态码或 `resp.ok` 判断成败 | **所有错误响应都可能是 HTTP 200 + 业务码**。必须解析 `code` 字段；`EnsureSuccessStatusCode()` 之类在此无效 |
| 24 | 把"缺少登录态"和"缺少设备指纹"当成一回事 | 两者独立。隐私模式 / 全新会话 / 清空 Cookie 会**同时**丢掉两者，使得归因失效（见 [9.5](#95-补充-352-的成因推断与验证方法)） |
| 25 | `-352` 一出现就换架构/上无头浏览器 | 先排除**廉价原因**：UA 自曝（`Electron/`、`okhttp`、`python-*`）、请求头互相矛盾。这两项在实践中占比不低，且修复成本远低于换架构 |
| 26 | 只按"未扫码/已扫码/成功"枚举轮询状态 | 状态集合未知（"拒绝确认"是否有独立码未确认）。未知状态应**记日志 + 继续轮询 + 受兜底超时约束**，不要静默丢弃 |
