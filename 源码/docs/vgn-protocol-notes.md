# VGN 2.4G 协议笔记（已打通）

目标：不打开 VGN VHUB 就读到键盘电量。这里记录**实测确认**的协议和踩过的坑。

## 结论：怎么读电量

HID 集合 `usagePage 0xFF60 / usage 0x61`（V98Pro V4 的 2.4G 接收器 = `mi_01&col02`），
一来一回都是 **63 字节帧**：

```
[0]     命令 commandId
[1]     参数 commandParam
[2]     0
[3]     总负载长度 totalPayloadLength
[4]     包序号 packetIndex
[5]     本包负载长度 payloadLength
[6..61] 负载（最多 56 字节）
[62]    校验和 = 255 - ((reportId + 前 62 字节之和) % 256)
```

- **电量查询 = 命令 `0x87`**，无负载
- 回包：`[5] = 负载长度(2)`，`[6] = 电量 %`，`[7] = 充电标志`
- Windows 上该集合只有 **一个合法输出报告 id `0x09`**，所以真实写入是
  `[0x09] + 63 字节帧` = 64 字节；`reportId` 也要参与校验和
- 回包（64 字节，含报告 id）解析时所有下标 **+1**：`reply[6]=负载长度`,
  `reply[7]=电量`, `reply[8]=充电`

实测：`09 87 00 00 00 00 02 4C 00 … 21` → `0x4C = 76%`，与 VGN VHUB 一致。

## 协议参数来源（网页驱动）

`https://hub.vgnlab.com/` 的 `assets/vendor-bytech-*.js` 里能直接读到配置对象：

```js
const cn = { totalPacketSize: 63, headerLength: 6, tailLength: 1,
             commandIdIndex: 0, commandParamIndex: 1,
             totalPacketLengthIndex: 3, currentPacketIndex: 4,
             lengthIndex: 5, checksumIndex: 62 };
```

SDK 里电量读取就是 `protocol.encode(135)`（`135 = 0x87`），回包取前两个字节
`{level, charging}`。设备寻址表（VHUB 与网页驱动一致）：

| 设备 | VID / PID | usagePage / usage | 路径 |
| --- | --- | --- | --- |
| V98Pro V4 1K 2.4G Dongle | `0x391D` / `0x2A04` | `0xFF60 / 0x61` | `mi_01&col02` |
| V98Pro V4 1K 有线 | `0x391D` / `0x200C` | `0xFF60 / 0x61` | `mi_02&col01` |
| V98Pro V4 8K / TIMI | `0x391D` / `0x2013`… | `0xFF60 / 0x61` | `mi_03…` |
| V87Pro V2 | `0x391D` / `0x2015`… | `0xFF60 / 0x61` | reportId `9` |
| Dragonfly3 Pro（有线） | `0x391D` / `0x1003` | `0xFF01 / 0x10` | 鼠标命令集 |

## 踩过的坑

1. **校验和必须含 reportId**。少了 `+reportId` 时，dongle 不回数据，而是把发来的帧
   原样退回并把 `[62]` 改成它算出的值（例如 `255-(9+0x0D+0x01)=0xE8`）。
   这个"回显带校验和"极容易被误判成"设备没数据/键盘睡眠"。
2. **reportId 不是 0**。厂商 WebHID 代码写的是 `sendReport(0, …)`（无编号报告语义），
   但 Windows 上该集合只接受输出报告 id `0x09`；用别的 id 一律 `ERROR_INVALID_PARAMETER`。
3. **`0x0D/0x01`（三模基本信息）不是电量命令**。那是 VHUB 里另一套
   `@sparklinkplayjoy/sdk-keyboard-v2` 的用法；网页驱动用的 bytech SDK 走 `0x87`。
4. **同一台 dongle 有多个 HID 集合，产品名相同**。遍历时必须先试命令通道
   （`0xFF60/0x61`），否则别的集合会先把名字占掉。

## 调试手法（可复用）

1. **把厂商 SDK 跑在 Node 里**（`tools/vgn-recon/oracle.js`）：
   `Object.defineProperty(globalThis,'navigator',{value:{hid:{…}}})` 覆盖只读的
   `navigator`，塞一个假的 `navigator.hid`，把 `sendReport` 转成 node-hid 的真实写入。
   注意包声明 `"type":"module"` 但 `dist/cjs` 是 CommonJS，要复制成 `.cjs` 再 require。
2. **本地 WebHID 探测页**（`tools/webhid-probe/`）：一个单页 + 迷你 TCP 服务器，
   浏览器里 `navigator.hid.requestDevice` 一次授权后，页面把每次 `sendReport` 的回包
   POST 回服务器写成日志。Chromium 会替你把非法 report id / 长度直接抛
   `NotSupportedError`，比自己猜快得多。
3. 关键技巧：**拿设备的真实值当校验点**（这次是 VHUB 显示的 76%），
   在回包里搜 `0x4C`，一眼就能确认哪一版帧是对的。

## 还没做的

- 鼠标系列（`@bytech/mouse-uni`，`0xFF01/0x10`）是另一张命令表，需要单独接。
- 键盘睡眠时 dongle 会回空负载（`payloadLength < 2`），工具按"读不到"处理，
  不显示 0%。
