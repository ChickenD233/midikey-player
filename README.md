# MidiKeyPlayer（MIDI 按键播放器）

**简体中文** | [English](#midikeyplayer-midi-key-player)

> 新手先看：[快速上手](docs/快速上手.md)（2 分钟）· [图文教程](docs/图文教程.md)（5 分钟，带截图）

导入 MIDI。程序把音符转换成键盘与鼠标按键输出，发送到前台窗口。
内置谱面编辑器。你可以先改谱，再播放。

- 系统：Windows 10 / 11（64 位）
- 单文件 exe。不需要安装 .NET
- 界面语言：中文

> ⚠️ 虚拟输入可能违反第三方软件的使用规则，有账号封禁风险。请只在练习、测试或单机场景使用。后果由使用者承担。

> **免费声明**：本程序完全免费、开源，没有收费版本，作者也从没卖过它。
> **如果你是「购买」的此软件，立刻退款，你被骗了。**
>
> - 作者 B 站：<https://space.bilibili.com/28440883>
> - 反馈 QQ 群：**1042477909**
> - 源码与全部版本：<https://github.com/ChickenD233/midikey-player>
>
> 第一次使用会问一次「作者的 B 站 ID」（答案就是作者在 B 站的名字，看看上面的主页就知道）。答对一次就不再问。

## 下载

1. 打开右侧 **Releases**。
2. 下载 `MidiKeyPlayer-win-x64-<版本>.zip`（取最新版本号），约 17 MB。
3. 解压。里面有 `MidiKeyPlayer.exe`（约 23 MB）与 `更新日志.txt`。
4. 双击 `MidiKeyPlayer.exe`。
5. 系统弹出 UAC，选「是」。
6. 如果弹出 SmartScreen「未知发布者」，选「更多信息」，再选「仍要运行」。
7. 杀毒软件可能误报「模拟按键」。请把它加入白名单。

首次启动会慢几秒：程序是单文件，启动前先把原生库解压到磁盘。
解压目录是 `%TEMP%\.net`。清理临时目录之后，下次启动会再解压一次。
加白名单时，请把 `MidiKeyPlayer.exe` 与 `%TEMP%\.net` 一起加。只加 exe，解压出来的 DLL 仍可能被拦，
表现是启动失败，而且没有明显提示。

程序是自包含的单文件 exe。不需要安装 .NET。
更新说明、第三方声明与 MIT 许可都打包在 exe 里。解压后看不到这些松散文件。
当前版本没有打开它们的界面入口。要看内容，请打开仓库里的同名源文件。

`更新日志.txt` 在包里，不在 exe 里。它按版本从新到旧累积，每次发新版只在最上面加一节。
看某一版改了什么，就打开这份文件。源文件是 `MidiKeyPlayer/docs/更新日志.txt`。

## 使用

1. 点「打开 MIDI 文件 / 文件夹」，选一个文件，或选一个文件夹让程序列出里面的曲目。支持 `.mid`、`.midi`、`.kar`、`.rmi`。
2. 选了文件夹之后，左栏在声轨列表上方会多出一块「文件夹曲目」区域，列出这个文件夹里的 MIDI，点哪首载入哪首，右上角「关闭」收起。按钮右边的箭头菜单里还有「最近打开」。
   曲目多时默认只列 50 行，末尾一条「显示其余 N 项」点一下就把这一份列表整份列出来；上面还有一个搜索框，按名字过滤（只搜当前文件夹，不进子文件夹）。
3. 在左侧点一行，作为主旋律（选中的行带 ● 标记）。打击乐轨也可以选：有的目标乐器自带鼓组。想一起出声，就在左侧勾多个声部。
4. 需要改谱时，在卷帘上直接改。做法见下。
5. 调速度、移调、倒计时。
6. 点「▶ 播放」，或按 **F6**。
7. 在倒计时结束前切到目标程序。

常用项就在右边。设备接入、输入兼容、热键、导出按键表、键位方案这些不常改的，都收在一个「设置」窗口里，主界面只留一个按钮。播放前自检不在设置里：它常驻在主界面右上方的状态卡。

播放中，你可以拖卷帘或进度条跳转。也可以实时改速度和移调。暂停时可以直接打开另一首换歌。

## 卷帘编辑

卷帘画出整首旋律。上方是时间标尺，按文件的拍号与速度画小节线。左侧是琴键栏。

| 操作 | 作用 |
|---|---|
| 标尺点 / 拖 | 定位播放位置 |
| 点左侧琴键 | 选中该音高的全部音符。按住拖动可连选多个音高 |
| 拖音符中部 | 移动。多选时整组一起动。左右改时间，上下改音高 |
| 拖音符任一端 | 改长度。左端改头，右端改尾。音高不变 |
| 空白处拖 | 框选多个音符 |
| 空白处单击 | 定位，并清空选择 |
| 双击空白 | 加一个音。按住继续拖可一次定好长度 |
| 右键 | 删音。按住拖动可连续擦除 |
| Delete | 删除选中的音 |
| Ctrl+A / Esc | 全选 / 清空选择 |
| 方向键 | 微调。左右按吸附步长，上下按半音。按住 Shift 加速 |
| Ctrl+Z / Ctrl+Y | 撤销 / 重做。保留当前缩放位置 |
| 滚轮 | 以光标为锚点缩放 |
| Shift+滚轮 | 左右平移 |
| 中键拖动 | 左右平移 |
| 缩小 / 放大 / 全曲 | 工具栏按钮。右边显示当前缩放百分比 |
| 帮助 | 展开 / 收起右侧的操作说明 |

**颜色。** 每条声轨一个唯一颜色。左侧列表的文字颜色与卷帘里的音符颜色一致。没参与演奏的声轨用灰（打击乐轨稍深一点）。当前键位方案里没有对应键的音显示为灰色，演奏时会跳过。

**吸附。** 勾选「吸附 0.1s」后，拖动与加音按 0.1 秒对齐。吸附的是音符的头或尾，不是光标。

**改动只存在内存里。** 要留档，点「导出 MIDI…」。程序写出一个标准 MIDI 文件。

**换轨会丢改动。** 切换主旋律轨，或改合奏勾选，程序会丢弃手动改动。日志会写明。想保住改动，先导出 MIDI。

## 试听

点「试听」按钮，用 Windows 自带的 MIDI 合成器播放当前谱面。再点一下停止。

- 它和演奏完全分开：不发送按键，不走倒计时，不会最小化窗口。
- 播放中随时可以拖进度条或卷帘定位。松手就从新位置接着放。
- 不需要安装任何音频软件。设备不可用时按钮会自动变灰，日志里写明原因。

## 选项

主界面右下角分两张卡。**演奏参数**只改怎么弹，不动谱面。**谱面调整**会改当前谱面，卷帘里看得到。

- **播放前自检**。常驻在主界面右上方的状态卡：管理员权限与输入法状态，通过打勾、未通过打叉。切换中英文模式后点「刷新」重新检测。不想要就在设置里关掉（默认开）。
- **合奏**。在左侧勾选多个声部一起演奏。编号 1 最优先。乐器一次只发一个音，冲突时先演奏编号小的。
- **去除开头空拍**。默认开。剪掉开头的休止，让旋律从第 0 秒开始。卷帘左下角会标出剪掉多少秒。
- **输入兼容**。三档：稳健、标准、极限。目标程序按帧读按键。修饰键与音键的间隔小于一帧就会漏音。掉帧的机器选「稳健」。
- **播放后自动最小化窗口**。默认开。
- **播放悬浮窗**。默认开。倒计时与演奏期间在屏幕右上角显示一个置顶小窗：倒计时是大号秒数，
  开始演奏后是**滚动迷你卷帘**——可见窗口 8 秒，黄色播放头固定在四分之一处，
  音符从右向左滚过去（右边是马上要弹的音）。下方是进度条与时间；暂停、循环遍数标在右下角。
  切到目标程序后也看得到。悬浮窗可拖动，位置自动记忆。
- **暂停后关闭悬浮窗**。默认开。暂停（F6 或焦点离开目标程序自动暂停）时把悬浮窗收起来，不再挡着画面；
  继续演奏时自动弹回来，不用去设置里重开。不想要就在 设置 → 常规 → 界面 里取消勾选。
  悬浮窗右上角的 ✕ 只让**本次播放**不再弹它，设置里的开关不动，下次播放自动恢复。
  想永久关掉，仍然在 设置 → 常规 → 界面 里取消「播放悬浮窗」。
- **导出按键表**。导出 G HUB 脚本（.lua），或通用 CSV。内容跟随当前键位方案，与实际演奏一致。
- **循环**。默认关。勾上后一遍播完就从头再来，日志里写明当前是第几遍。
- **数值范围**。速度 10%–400%，默认 100%。移调 ±24 半音，默认 0。倒计时 0 / 3 / 5 / 10 秒，默认 3 秒。
- **设置…**。点这个按钮打开设置窗口，三页。
  **常规**页：设备接入、输入兼容、三个热键、导出按键表、界面（皮肤、自检开关）。
  **键位**页：方案、按键绑定、功能键。
  **赞助**页：置顶爱发电，下面列作者的 B 站与 GitHub。赞助完全自愿，不影响功能。
  改动即时生效，关掉窗口不影响设置。主界面因此不再堆这些项，卷帘拿到的高度也更多。

## 皮肤

皮肤分**浅色（白）**与**深色（黑）**两套，也可以选**自动**跟随 Windows 的浅色 / 深色设置。

1. 点「设置…」，切到**常规**页。
2. 在「界面」一栏的「皮肤」里选：自动（跟随系统）/ 浅色（白）/ 深色（黑）。
3. 选完立即生效，不用重启，也不用关窗。

默认是**自动**。设置存在 `settings.json` 的 `ThemeMode` 里（0 自动 / 1 浅色 / 2 深色）。
两套皮肤的颜色都写在 `MidiKeyPlayer/Styles/Theme.axaml`，深色那套的文字对比度都在 6:1 以上。

## MIDI 设备实时演奏

接上 MIDI 键盘或打击垫，按键直接发送到目标程序。不用先做谱。

1. 在选项区勾「MIDI 设备实时演奏」。
2. 在「设备」里选你的设备。点「刷新」重新扫描（支持热插拔）。
3. 切到目标程序，开始弹。

| 设置 | 作用 |
|---|---|
| 基准八度 | 设备上哪个八度是中音 do。默认中音 do（1） |
| 自动贴合音域 | 默认开。按最近弹过的音自动挑基准八度，跨几个八度弹也不会哑 |
| 力度下限 | 默认 1（不过滤）。设备有抖动或触后噪声时往上加 |

**音高怎么对。**

- 基准八度的 do re mi fa sol la si → `Z X C V B N M`（默认方案「8 键半音」）。
- 音域由当前方案决定。「21 键自然音」与「第五人格键位」不带功能键，只弹键位范围内的音。
- 想跨八度弹，就在键位设置里勾「启用功能键」，再绑升高八度与降低八度。
- 没有对应键的音不发声。状态文字与日志写明原因：超出音域，或键表里没有这个音。
- 乐器一次只发一个音。同时按住多个键时，后来的音优先，前一个音被换掉。
- 设备把同一个音重复发送时只发一次。键盘接触抖动、同一个音走多个通道、驱动重发都会这样；
  30ms 以内的重复 NoteOn 直接丢掉，日志里写明丢了多少次（用来确认设备真的在重发）。

「设备实时演奏」和「文件播放」互不影响。播放期间设备照样能弹，但两路输入会同时生效，
想干净一点就在播放时先取消勾选。状态文字实时显示最近一个音映射成了什么。
停止时日志给出统计：收到多少音、多少重复被丢掉、多少没有对应的键、多少被后来的音顶掉。

设置自动记忆。位置是 `%LOCALAPPDATA%\MidiKeyPlayer\settings.json`。日志在同一目录，文件名是 `play.log`。

## 热键

程序用系统低层键盘钩子。热键在目标程序中直接生效。

| 键 | 动作 |
|---|---|
| **F6** | 空闲 = 开始。播放中 = 暂停。暂停中 = 继续。倒计时中 = 取消 |
| **F5** | 后退 5 秒 |
| **F7** | 前进 5 秒 |
| **F8** | 上一首（文件夹曲目，到底回绕） |
| **F9** | 下一首（文件夹曲目，到底回绕） |

五个键都能改，也能关掉（选「无」）。在界面的「控制热键」「后退热键」「前进热键」「上一首」「下一首」里改。

**切歌热键**要先「打开文件夹」列出曲目才生效。播放中（含暂停中）切歌不走倒计时，直接接着弹新的一首；
空闲时只载入不自动播放。有未导出的卷帘改动时会拒绝切歌，防止改动被静默丢掉。

**注意**：播放中跳转可能让目标程序侧卡音。遇到卡音，按「停止」，再重新播放。最稳的用法是播放前用卷帘或进度条定位。

## 注意

1. 目标程序用**窗口化**或**无边框窗口化**。全屏独占收不到模拟按键。
2. 播放开始前，点一下目标窗口。目标程序必须在前台。
3. 输入法切到**英文**。中文输入法会截走按键。
4. 目标窗口内**原地不动**。按住 `W`、`A`、`S`、`D` 时，键盘矩阵会吞掉部分音键。这是键盘硬件限制，软件无法修复。只有全键无冲（NKRO）键盘可以避免。
5. 目标程序以管理员运行时，本程序也要提权（自检卡的「以管理员重启」一键搞定）；普通程序不需要提权。
6. 鼠标或键盘失控时，狂按 **F6**。

## 键位方案

程序内置六套方案。方案名就是下表的六个名字。

| 方案 | 键位 | 音域 | 功能键 |
|---|---|---|---|
| 8 键半音（默认） | 一排 `Z X C V B N M ,` = do..高音 do（自然音）。鼠标左键降八度、右键升八度、中键升半音 | `1.` ~ `#1˙` | 开，鼠标左、右、中三键 |
| 21 键自然音 | 下排 `Z X C V B N M` = 中音 do..si；中排 `A S D F G H J` 高一个八度；上排 `Q W E R T Y U` 再高一个八度 | `1` ~ `7˙˙` | 不用 |
| 21 键半音 | 下排 `Z X C V B N M` = 低音 do..si；中排 `A S D F G H J` = 中音；上排 `Q W E R T Y U` = 高音。按 `Shift` 升半音，按 `Ctrl` 降低半音 | 低音 do 再低半音 ~ `7˙` | 开，`Shift` 升半音、`Ctrl` 降半音 |
| 第五人格键位 | 低排 `, L . ; / I 9 O 0 P - [`（低八度）；中排 `Z S X D C V G B H N J M`（中音）；高排 `Q 2 W 3 E R 5 T 6 Y 7 U`（高八度）。每排 12 个半音 | `1.` ~ `7˙` | 不用 |
| 洛克王国手碟 | `T Y U` = 高音 do re mi（`1˙` ~ `3˙`）；`F G H J K` = 中音 mi fa sol la si（`3` ~ `7`）；`B` = 低音 la（`6.`）。只有自然音 | `6.` ~ `3˙` | 不用 |
| Roblox 钢琴键位 | 一个半音一条键位，共 61 条。白键同四排（`1..0` = C2..E3、`Q..P` = F3..A4、`A..L` = B4..C6、`Z..M` = D6..C7）；黑键写的是「同一个键 + `Shift`」，键帽上显示成 `! @ $ % ^ * (` 与 `Q W E …` | `1..` ~ `1˙˙`（C2..C7） | 不用 |

「21 键半音」与「8 键半音」都开着功能键：不按修饰键是自然音，按住就得到变化的音
（`Shift`+`Z` 升半音、`Ctrl`+`Z` 降半音，或按住鼠标中键再按 `Z`）。「8 键半音」的两个鼠标键还能整排上下挪一个八度。

「Roblox 钢琴键位」不用功能键：61 个音一个一条键位，黑键自己就是一条（写的是「下面那个白键 + `Shift`」）。
键帽上按游戏写谱子的习惯显示：白键字母小写（`z x c v b n m`），黑键是同一个键的上位字符（`Z X C V B N M`、`! @ $ % ^ * (`）。

表格里的音域用**简谱**：数字 1..7 是音级，1 = do。升降号写在数字前面，例如 `#4`。
数字后面加 `˙` 是高一个八度，加 `˙˙` 是再高一个八度。
数字后面加 `.` 是低一个八度，加 `..` 是再低一个八度。

**没有对应键的音不弹。** 音域里缺哪个半音，就跳过哪个音，不换音高。这条行为固定，界面里没有开关。
「第五人格键位」是完整半音阶：每个半音都有自己的键，不用八度键与升半音键。
「8 键半音」只有自然音键，半音靠鼠标中键补。
「Roblox 钢琴键位」的 61 条键位 = 36 个白键 + 25 个黑键：E、B 与最高的 C7 上面没有黑键，所以正好 25 条。

**功能键。** 「功能键」区块顶上有一个「启用功能键」勾选框。
勾上后，可以绑升高八度、降低八度、升半音、降低半音四个键。音域随之向上下各扩一个八度，
降半音键还会把最低音向下多扩一个半音。
不勾时该方案不用这四个键。超出键位范围的音直接不弹。

**半音键的取舍规则。** 同一个黑键「下方白键 + 升半音」与「上方白键 + 降半音」都能弹到时，
固定用升半音键。降半音键用在两处：方案没绑升半音键时补黑键，以及最低键下面那一个半音
（升半音键够不到）。「21 键半音」就是两个半音键都绑的方案。

**改键位。** 点主界面的「设置…」按钮，切到「键位」页。这一页分三块：

| 区块 | 作用 |
|---|---|
| 方案 | 选方案、新建方案…、改名…、导入方案…、导出方案…、管理 ▾（删除方案、恢复默认设置） |
| 按键绑定 | 按行排的键帽方块。块上大字是简谱音高，小字是唱名，键名在下半块，右上 `✕` 解绑 |
| 功能键 | 「启用功能键」勾选框，下面是升高八度、降低八度、升半音三个键 |

**按行排。** 一行里的键从左到右按音高排。行内不换行，太长就左右滚动。
21 键的两套方案就是整齐的三行七列，36 键那套就是整齐的三行十二列，Roblox 钢琴键位是四排（白键与它的黑键排在同一行）。
行尾的「+」在这一行末尾加一个音。最后一行下面的「+ 加一行」加一整行，音高比上一行高一个八度。

**等待按键。** 点按键方块后它变成「按一个键…」，按一下键盘或鼠标就绑上。按 Esc 取消。
`PageUp` 这类在键帽上按不到的键，点旁边的「选键名」从列表里选。
单独按一下 `Shift`、`Ctrl`、`Alt` 再松开，也能把它们绑成功能键。

**哪些音还没绑。** 窗口底部常驻一行统计，例如「绑了 21 条，3 行。能弹 1 到 7˙˙，其中 0 个音还没有绑键。（功能键关）」。

**音域由键位决定。** 不填数字。键能到哪，就能弹到哪。功能键关掉时，八度键与升半音键不参与。

**方案文件。** 内置方案不能改名、不能删掉。想改就先点「新建方案…」复制一份。
自定义方案在 `%LOCALAPPDATA%\MidiKeyPlayer\schemes\` 下，重启后仍在列表里。「导入方案…」「导出方案…」走单个 JSON 文件，方便互相分享。

**每个方案独立记忆。** 速度、移调、输入兼容档按键位方案名分别保存。

**与设备实时演奏的关系。** MIDI 设备输入的基准八度在「MIDI 设备实时演奏」区单独设置。
八度键与升半音键跟随当前方案的「启用功能键」。

## 常见问题

**目标程序里没反应，但在记事本能打字？**

按顺序检查四项。

1. 先点一下目标窗口。
2. 目标程序改成无边框窗口化。
3. 目标程序以管理员运行时，本程序也要提权（自检卡有「以管理员重启」）；普通程序不需要。
4. 输入法切英文。

**站着不动不漏音，一走位就漏音？**

这是键盘硬件的幽灵按键问题，不是软件问题。见上面「注意」第 4 条。

**偶尔缺音？**

播放结束时，窗口底部的消息条显示一行「时序诊断」。完整记录写在 `%LOCALAPPDATA%\MidiKeyPlayer\play.log`。手动点「停止」时，消息条会被「已停止。」覆盖，请直接看 play.log。

- 修饰键提前量不足：把「输入兼容」改成「稳健」。该档把提前量从 40ms 提到 70ms。
- 同键重触发偏多：换「稳健」档。该档把同键重触发间隔从 45ms 提到 80ms。速度不改变这个物理间隔。
- 时值被压到下限的音数不是 0：这首谱挤得比该档位允许的最快速度还快。换「稳健」档，或降速。

**MIDI 设备没反应？**

按顺序检查四项。

1. 设备在其它软件里被占用了就先关掉它（MIDI 输入通常只能被一个程序打开）。
2. 点「刷新」，确认设备出现在下拉框里。
3. 状态文字要显示「实时演奏中」。显示「启动中…」不动就是打开失败，日志里有原因。
4. 弹的音没有对应的键。看状态文字：显示「没有对应的键」就调「基准八度」，或勾上「自动贴合音域」。

**点窗口的 × 会怎样？**

彻底退出。程序停止播放，松开所有按键，注销热键。只想暂时离开目标窗口，就最小化窗口。托盘菜单可以控制开始、暂停、停止和退出。

## 目录

```
MidiKeyPlayer/              # 主程序（Avalonia + .NET 8）
MidiKeyPlayer/docs/更新说明.txt  # 本说明书（打包时嵌进 exe）
MidiKeyPlayer/docs/更新日志.txt  # 累积更新日志（打包时进 zip）
MidiKeyPlayer/build-win.sh  # 发布脚本：打包成单文件 exe 的 zip
MidiKeyPlayer/release/      # 发布产物：zip 与解出的 exe（打包生成）
示例MIDI/                   # 开发用示例曲目（不进发布包）
tools/run-selftest.ps1      # 跑一次内置自检，统计 PASS / FAIL
tools/release.ps1           # 发新版：版本号加一、写日志、构建、验证、发 Release
LICENSE                     # MIT 许可
THIRD-PARTY-NOTICES.md      # 第三方组件许可声明
```

## 自检（开发用）

程序带一个纯逻辑自检，覆盖键位方案与简谱换算。它不建窗口、不注册热键、不碰按键与 MIDI 设备。

1. 构建 Release 版：`dotnet build MidiKeyPlayer/MidiKeyPlayer.csproj -c Release`。
   csproj 设了 `RuntimeIdentifier=win-x64`，构建输出在 `MidiKeyPlayer\bin\Release\net8.0\win-x64\`。
2. 跑 `tools/run-selftest.ps1`。脚本按优先级自动找 exe：`bin\Release\net8.0\win-x64\`、`release\win-x64\`、`bin\Release\net8.0\`。
   然后设环境变量、等程序退出、读回报告、统计 PASS / FAIL。要指定别的 exe，就用 `-ExePath <路径>`。
3. 退出码 0 = 全部通过，1 = 有用例失败。脚本的其它退出码见脚本头部说明。

当前 29 条断言全部通过，退出码 0。原来是 36 条：其中 13 条测的是自检内部的一份键名翻译副本，
副本已删，这 13 条一起删。

也可以手动设 `MIDIKEY_GAME_SELFTEST=<报告文件路径>` 再启动程序。程序默认不提权，自检进程不需要管理员权限。

## 自己打包（开发用）

普通用户不需要这一节。只有自己从源码打包才要装：

- **.NET 8 SDK**：编译程序。
- **Python 3**：打包脚本用它写 zip，要求标准库 `zipfile` 可用。找不到可用的 Python 就不出包。

打包命令是 `bash MidiKeyPlayer/build-win.sh`，产物在 `MidiKeyPlayer/release/` 下。

发新版走发版脚本 `tools/release.ps1`。它把下面这些事一次做完：

1. 版本号补丁号 +1（`-Bump Minor` / `-Bump Major` 可改）。
2. 在 `MidiKeyPlayer/docs/更新日志.txt` 最上面加新版本一节。
   不给 `-Notes` 时，用上个 tag 以来的提交标题当草稿。
3. 改 `MidiKeyPlayer/MidiKeyPlayer.csproj` 的 `<Version>` 与 `更新说明.txt` 第一行的版本号。
4. 跑 `build-win.sh` 出包。zip 里是 exe 与 `更新日志.txt`。
5. 跑内置自检，退出码必须是 0。
6. 跑主窗、设置窗口（常规、键位、赞助三页）与深色皮肤主窗的界面快照，确认都能出图。
   设置窗那几步会自己走一遍「开 → 关 → 再开」，顺带验证内容归属来回搬是干净的。
7. 跑文件夹曲目卡换歌回归：连点三首，每步都要换过去，列表行数不能塌。
8. 提交、推 main、打 tag、建 Release、上传 zip。上传后比对远端资产的 sha256 与本地 zip，
   不一致就报错。

```
powershell -File tools\release.ps1 -DryRun                     # 只看会发什么
powershell -File tools\release.ps1 -Notes "（这一版改了什么）"   # 自己写日志
powershell -File tools\release.ps1 -SkipPush                   # 只构建验证，不提交不发布
powershell -File tools\release.ps1 -TrimParity                 # 加做裁剪比对
```

规则：每次发 release 一律发新版本。已经发布的包不动、不覆盖、不重传。
发版全程自动，脚本一路做到上传完成，中途不停下来问。
tag 已存在，脚本直接停手。日志里没有当前版本号那一节，`build-win.sh` 拒绝打包。

发布包开了裁剪，设置写在 `MidiKeyPlayer.csproj` 里。裁剪会让 exe 从 45.6 MB 降到 23.0 MB。
反射相关的程序集（`MidiKeyPlayer`、`Avalonia` 系列、`Melanchall.DryWetMidi`）用
`TrimmerRootAssembly` 钉住，类型与成员一个不删。

动过裁剪设置或升级依赖之后，发版时加 `-TrimParity`。它会额外构建一份不裁剪的 exe，
把两张界面快照逐字节比对。比对失败就不发。
更细的曲线验证（挂 Avalonia 日志监听器查绑定错误）见 `midikey-audit\fixes\58-trim-verify.md`。

## 自动更新

启动时后台检查新版本（默认开启，v1.0.10 起支持自动下载）。发现新版时窗口顶部出现提示条：

- **左键点提示条**：自动下载更新包并显示进度；下载完成后再点一次，程序退出、自动覆盖旧文件并重启到新版。
- **下载中再点一次**：取消下载。
- **右键点提示条**：跳过这个版本，下个版本仍会提示。
- 下载失败或校验失败时点提示条可重试；没有更新包直链时退回「打开下载页」手动下载。
- 更新前如果卷帘里有未导出的改动，会提示先导出 MIDI。
- 更新重启后，新版首次启动会在 play.log 写一行「已从 vX 更新到 vY」，确认替换成功。

设置 → 常规 → 关于 里有当前版本号、「检查更新」按钮，以及更新说明 / 第三方声明 / 许可证的查看入口。
这一页还有免费声明与作者入口（B 站主页、爱发电赞助、复制 QQ 群号）。赞助页在 设置 → 赞助。

更新包只从本仓库的 Releases 下载（地址白名单），并校验包内 exe 完整后才替换。
要关掉检查，把 `MidiKeyPlayer/Engine/AutoUpdate.cs` 里的 `Enabled` 改成 `false`。

## 设置迁移

首次启动时，如果新目录没有设置文件，程序会尝试读旧目录 `%LOCALAPPDATA%\HarpAutoPlayer`，
把速度、移调、热键与 MIDI 设备设置迁移过来。迁移失败只写日志，不打断启动。

## 技术说明

- 界面：Avalonia（.NET 8）。MIDI 解析与设备接入：DryWetMidi。
- 发布包：单文件、自包含、开裁剪，反射相关的程序集钉住不裁。zip 约 17 MB，exe 约 23 MB。
- 第三方组件许可见 `THIRD-PARTY-NOTICES.md`。这份文本嵌在 exe 里。
- 输入：Windows SendInput，扫描码模式。全局热键：WH_KEYBOARD_LL。
- 试听：Windows 自带的 winmm MIDI 输出。没有第三方音频库。
- 时序余量按**物理毫秒**给，不随播放速度缩放。标准档：修饰键比音键早至少 40ms，文件播放时每个音至少按住 17.7ms（MIDI 设备实时演奏路径是 46ms），同键重触发至少隔 45ms；提前派发量也是固定物理时间。
- 10% 与 400% 下，这些物理间隔与 100% 完全相同（实测 40ms / 17.7ms）。变的是它们对应的音乐时间长度：快放时同一段物理时间覆盖的音乐更长，慢放时更短。
- 结果是快放不再因为间隔被压到一帧以内而漏音。代价是慢放时发音比谱面整体稍晚一点，松手也稍晚一点。
- 音符之间用「槽位」排开：与前音重叠的音顺延到前音之后，不靠缩短前音让位。这样不会产生零时长按键。
- 卷帘坐标换算只有一套。渲染与命中共用，两个方向对称。

## 许可证

本程序用 MIT 许可。仓库根目录有 `LICENSE`。
第三方组件许可见 `THIRD-PARTY-NOTICES.md`。
这两份文本都打包在 exe 里，随程序一起分发。当前版本没有打开它们的界面入口。

## 免责声明

本工具仅供学习和个人使用。因使用产生的账号处罚或其它后果，由使用者承担。


---

# MidiKeyPlayer (MIDI Key Player)

[简体中文](#midikeyplayermidi-按键播放器) | **English**

> New here? See the quick start (2 min): [docs/快速上手.md](docs/快速上手.md), or the illustrated tutorial (5 min): [docs/图文教程.md](docs/图文教程.md) (both Chinese, with screenshots).

Import a MIDI file. The program converts the notes into keyboard and mouse key output and sends it to the foreground window.
A built-in score editor lets you edit the score before playing.

- OS: Windows 10 / 11 (64-bit)
- Single-file exe. No .NET installation required
- UI language: Chinese

> ⚠️ Simulated input may violate the terms of use of third-party software and carries a risk of account bans. Use it only for practice, testing, or single-machine scenarios. You assume all consequences.

> **Free software notice**: This program is completely free and open source. There is no paid edition, and the author has never sold it.
> **If you "bought" this software, ask for a refund immediately — you were scammed.**
>
> - Author on Bilibili: <https://space.bilibili.com/28440883>
> - Feedback QQ group: **1042477909**
> - Source and all releases: <https://github.com/ChickenD233/midikey-player>
>
> On first use, the program asks once for the author's Bilibili ID (the answer is the author's Bilibili name — the page above shows it). Answer once and it never asks again.

## Download

1. Open **Releases** on the right.
2. Download `MidiKeyPlayer-win-x64-<version>.zip` (pick the latest version number), about 17 MB.
3. Extract it. Inside are `MidiKeyPlayer.exe` (about 23 MB) and `更新日志.txt` (changelog).
4. Double-click `MidiKeyPlayer.exe`.
5. When the UAC prompt appears, choose "Yes".
6. If SmartScreen shows "unknown publisher", choose "More info", then "Run anyway".
7. Antivirus software may flag "simulated keystrokes" as a false positive. Add it to the whitelist.

The first launch takes a few extra seconds: the program is a single file and extracts its native libraries to disk before starting.
The extraction directory is `%TEMP%\.net`. After you clean the temp directory, the next launch extracts once more.
When whitelisting, add `MidiKeyPlayer.exe` and `%TEMP%\.net` together. If you only whitelist the exe, the extracted DLLs may still be blocked —
the symptom is a launch failure with no obvious message.

The program is a self-contained single-file exe. No .NET installation required.
The release notes, third-party notices, and MIT license are all packaged inside the exe; you will not see these loose files after extraction.
The current version has no UI entry to open them. To read them, open the source files of the same name in the repository.

`更新日志.txt` ships in the package, not inside the exe. It accumulates versions from newest to oldest; each release only adds one section at the top.
To see what changed in a version, open this file. The source file is `MidiKeyPlayer/docs/更新日志.txt`.

## Usage

1. Click "Open MIDI File / Folder" (打开 MIDI 文件 / 文件夹), pick a file, or pick a folder to have the program list the songs inside. Supports `.mid`, `.midi`, `.kar`, `.rmi`.
2. After selecting a folder, a "Folder Songs" (文件夹曲目) area appears above the track list in the left column, listing the MIDI files in that folder — click one to load it; "Close" (关闭) at the top right collapses it. The arrow menu next to the button also has "Recently Opened" (最近打开).
   With many songs, only 50 rows are listed by default; a "Show the remaining N" (显示其余 N 项) row at the end expands the whole list in one click. A search box above the list filters by name (current folder only, no recursion into subfolders).
3. Click a row on the left as the main melody. The green dot marks the row recommended by the program. Percussion tracks can also be selected: some target instruments come with a drum kit. To sound multiple parts together, tick multiple tracks on the left.
4. When you need to edit the score, edit it directly on the piano roll. See below for how.
5. Adjust tempo, transposition, and countdown.
6. Click "Play" (▶ 播放), or press **F6**.
7. Switch to the target program before the countdown ends.

The common items sit right on the side. The ones you rarely change — device connection, input compatibility, hotkeys, key table export, key layout scheme — are all tucked into a single "Settings" (设置) window, and the main window keeps only one button. The pre-play self-check is not in Settings: it lives permanently in the status card at the top right of the main window.

During playback you can drag the piano roll or the progress bar to seek. You can also change tempo and transposition in real time. While paused you can directly open another song to switch.

## Piano Roll Editing

The piano roll draws the whole melody. The top is a time ruler, with bar lines drawn from the file's time signature and tempo. The left side is a piano-key strip.

| Action | Effect |
|---|---|
| Click / drag on the ruler | Move the play position |
| Click a piano key on the left | Select all notes of that pitch. Hold and drag to select multiple pitches in a row |
| Drag the middle of a note | Move it. With a multi-selection the whole group moves. Left/right changes time, up/down changes pitch |
| Drag either end of a note | Change length. The left end moves the head, the right end moves the tail. Pitch unchanged |
| Drag on empty space | Box-select multiple notes |
| Single-click empty space | Seek, and clear the selection |
| Double-click empty space | Add a note. Keep holding and drag to set its length in one go |
| Right button | Delete notes. Hold and drag to erase continuously |
| Delete | Delete the selected notes |
| Ctrl+A / Esc | Select all / clear selection |
| Arrow keys | Nudge. Left/right by the snap step, up/down by semitone. Hold Shift to speed up |
| Ctrl+Z / Ctrl+Y | Undo / redo. Keeps the current zoom position |
| Wheel | Zoom anchored at the cursor |
| Shift+wheel | Pan left/right |
| Middle-button drag | Pan left/right |
| Zoom out / zoom in / whole song | Toolbar buttons. The current zoom percentage is shown on the right |
| Help | Expand / collapse the operation help on the right |

**Colors.** Each track gets one unique color. The text color in the left list matches the note color in the piano roll. Tracks not taking part in the performance use gray (percussion tracks slightly darker). Notes that have no corresponding key in the current key layout scheme are shown in gray and are skipped during performance.

**Snap.** With "Snap 0.1s" (吸附 0.1s) ticked, dragging and adding notes align to 0.1 seconds. What snaps is the note's head or tail, not the cursor.

**Edits live only in memory.** To keep them, click "Export MIDI…" (导出 MIDI…). The program writes out a standard MIDI file.

**Switching tracks discards edits.** Switching the main melody track, or changing the ensemble ticks, makes the program discard manual edits. The log says so. To keep your edits, export MIDI first.

## Preview

Click the "Preview" (试听) button to play the current score with the MIDI synthesizer built into Windows. Click again to stop.

- It is completely separate from performing: it sends no keystrokes, runs no countdown, and does not minimize the window.
- During playback you can drag the progress bar or the piano roll to seek at any time. Release, and it continues from the new position.
- No audio software needs to be installed. When the device is unavailable the button grays out automatically, and the log states the reason.

## Options

The bottom right of the main window is split into two cards. **Performance Parameters** (演奏参数) only changes how it plays, without touching the score. **Score Adjustment** (谱面调整) modifies the current score, visible in the piano roll.

- **Pre-play self-check** (播放前自检). Lives permanently in the status card at the top right of the main window: administrator privilege and input method status, a check mark for pass and a cross for fail. After switching between Chinese and English input modes, click "Refresh" (刷新) to re-test. If you don't want it, turn it off in Settings (default on).
- **Ensemble** (合奏). Tick multiple parts on the left to perform together. Number 1 has the highest priority. The instrument emits only one note at a time; on conflict the smaller number plays first.
- **Trim leading empty beats** (去除开头空拍). Default on. Cuts the rests at the start so the melody begins at second 0. The bottom left of the piano roll marks how many seconds were cut.
- **Input compatibility** (输入兼容). Three levels: Safe (稳健), Standard (标准), Extreme (极限). The target program reads keys per frame. If the gap between a modifier key and a note key is smaller than one frame, notes get dropped. On a machine that drops frames, choose "Safe".
- **Auto-minimize window after play** (播放后自动最小化窗口). Default on.
- **Playback overlay** (播放悬浮窗). Default on. During the countdown and the performance, a top-most small window shows at the top right of the screen: the countdown is a large second count;
  once playing it becomes a **scrolling mini piano roll** — a visible window of 8 seconds, a yellow playhead fixed at the quarter position,
  notes scrolling from right to left (the ones on the right are about to be played). Below are a progress bar and the time; pause and loop pass count are marked at the bottom right.
  It stays visible after you switch to the target program. The overlay is draggable, and its position is remembered automatically.
- **Close the overlay on pause** (暂停后关闭悬浮窗). Default on. Pausing (F6, or the automatic pause when focus leaves the target program) puts the overlay away so it stops covering the screen;
  it comes back by itself when the performance resumes, with no trip to Settings. Untick it in Settings → General → Interface (设置 → 常规 → 界面) to keep it on screen while paused.
  The ✕ on the overlay only hides it for the **current playback**; the setting is untouched and the overlay returns on the next playback.
  To turn it off for good, still untick "Playback overlay" (播放悬浮窗) in Settings → General → Interface.
- **Export key table** (导出按键表). Export a G HUB script (.lua), or a generic CSV. The content follows the current key layout scheme and matches the actual performance.
- **Loop** (循环). Default off. When ticked, playback restarts from the beginning after one pass, and the log states which pass it is on.
- **Value ranges.** Tempo 10%–400%, default 100%. Transposition ±24 semitones, default 0. Countdown 0 / 3 / 5 / 10 seconds, default 3 seconds.
- **Settings…** (设置…). Click this button to open the Settings window, with three pages.
  The **General** (常规) page: device connection, input compatibility, three hotkeys, key table export, interface (skin, self-check switch).
  The **Keys** (键位) page: scheme, key bindings, function keys.
  The **Sponsor** (赞助) page: Afdian (爱发电) pinned at the top, with the author's Bilibili and GitHub below. Sponsoring is optional and changes nothing about the program.
  Changes take effect immediately; closing the window does not affect the settings. The main window therefore no longer piles up these items, and the piano roll gets more height.

## Skins

Two skins: **Light (white)** (浅色（白）) and **Dark (black)** (深色（黑）); you can also choose **Auto** (自动) to follow the Windows light / dark setting.

1. Click "Settings…" (设置…) and switch to the **General** (常规) page.
2. In the "Interface" (界面) column, under "Skin" (皮肤), choose: Auto (follow system) / Light (white) / Dark (black).
3. It takes effect immediately — no restart, and no need to close the window.

The default is **Auto**. The setting is stored in `ThemeMode` in `settings.json` (0 auto / 1 light / 2 dark).
The colors of both skins are defined in `MidiKeyPlayer/Styles/Theme.axaml`; the text contrast of the dark skin is above 6:1 everywhere.

## MIDI Device Live Performance

Connect a MIDI keyboard or pad, and key presses are sent directly to the target program. No need to make a score first.

1. In the options area, tick "MIDI Device Live Performance" (MIDI 设备实时演奏).
2. Choose your device under "Device" (设备). Click "Refresh" (刷新) to rescan (hot-plug supported).
3. Switch to the target program and start playing.

| Setting | Effect |
|---|---|
| Base octave (基准八度) | Which octave on the device is middle do. Default: middle do (1) |
| Auto-fit range (自动贴合音域) | Default on. Automatically picks the base octave from recently played notes, so playing across several octaves never goes silent |
| Velocity floor (力度下限) | Default 1 (no filtering). Raise it when the device has jitter or aftertouch noise |

**How pitches map.**

- Base octave do re mi fa sol la si → `Z X C V B N M` (default scheme "8-key chromatic" / 8 键半音).
- The range is determined by the current scheme. "21-key diatonic" (21 键自然音) and "Identity V layout" (第五人格键位) have no function keys and only play notes within the layout range.
- To play across octaves, tick "Enable function keys" (启用功能键) in the key settings, then bind octave up and octave down.
- Notes with no corresponding key stay silent. The status text and the log state the reason: out of range, or the note is not in the key table.
- The instrument emits only one note at a time. When multiple keys are held at once, the later note wins and the earlier one is replaced.
- When the device sends the same note several times, only one note goes out. Key contact chatter, one note on several channels, and driver re-sends all look like this;
  a repeated NoteOn within 30 ms is dropped, and the log states how many were dropped (use it to confirm the device really re-sends).

"Device live performance" and "file playback" do not interfere with each other. The device can still be played during playback, but both input paths take effect at the same time —
untick it during playback if you want it clean. The status text shows in real time what the most recent note mapped to.
On stop, the log gives statistics: how many notes were received, how many duplicates were dropped, how many had no corresponding key, and how many were replaced by later notes.

Settings are remembered automatically. Location: `%LOCALAPPDATA%\MidiKeyPlayer\settings.json`. The log is in the same directory, file name `play.log`.

## Hotkeys

The program uses a system low-level keyboard hook. Hotkeys take effect directly inside the target program.

| Key | Action |
|---|---|
| **F6** | Idle = start. Playing = pause. Paused = resume. Countdown = cancel |
| **F5** | Back 5 seconds |
| **F7** | Forward 5 seconds |
| **F8** | Previous song (folder songs, wraps around at the end) |
| **F9** | Next song (folder songs, wraps around at the end) |

All five keys can be changed or disabled (choose "None" / 无). Change them under "Control Hotkey" (控制热键), "Back Hotkey" (后退热键), "Forward Hotkey" (前进热键), "Previous Song" (上一首), and "Next Song" (下一首) in the UI.

**Song-switch hotkeys** only work after you "Open Folder" (打开文件夹) to list the songs. Switching songs during playback (including while paused) skips the countdown and continues directly with the new song;
while idle it only loads without auto-playing. With unexported piano-roll edits, song switching is refused, to prevent edits from being silently lost.

**Note**: seeking during playback may cause stuck notes on the target program's side. If a note sticks, press "Stop" (停止) and play again. The most robust usage is to seek with the piano roll or progress bar before playing.

## Cautions

1. Use **windowed** or **borderless windowed** mode for the target program. Exclusive fullscreen cannot receive simulated keystrokes.
2. Before playback starts, click the target window once. The target program must be in the foreground.
3. Switch the input method to **English**. A Chinese input method intercepts the keystrokes.
4. **Stand still** inside the target window. While holding `W`, `A`, `S`, `D`, the keyboard matrix swallows some note keys. This is a keyboard hardware limitation that software cannot fix. Only full NKRO (n-key rollover) keyboards avoid it.
5. The program forces itself to start as administrator. When the target program runs as administrator, this program must also be administrator. Otherwise Windows blocks the keystrokes.
6. If the mouse or keyboard goes out of control, mash **F6**.

## Key Layout Schemes

The program ships with six built-in schemes. The scheme names are the six names in the table below.

| Scheme | Keys | Range | Function keys |
|---|---|---|---|
| 8-key chromatic (default) (8 键半音) | One row `Z X C V B N M ,` = do..high do (natural notes). Left mouse button lowers an octave, right raises an octave, middle raises a semitone | `1.` ~ `#1˙` | On; left, right, and middle mouse buttons |
| 21-key diatonic (21 键自然音) | Bottom row `Z X C V B N M` = middle do..si; middle row `A S D F G H J` one octave higher; top row `Q W E R T Y U` another octave higher | `1` ~ `7˙˙` | Not used |
| 21-key chromatic (21 键半音) | Bottom row `Z X C V B N M` = low do..si; middle row `A S D F G H J` = middle; top row `Q W E R T Y U` = high. Hold `Shift` to raise a semitone, hold `Ctrl` to lower a semitone | one semitone below low do ~ `7˙` | On; `Shift` raises a semitone, `Ctrl` lowers a semitone |
| Identity V layout (第五人格键位) | Low row `, L . ; / I 9 O 0 P - [` (low octave); middle row `Z S X D C V G B H N J M` (middle); high row `Q 2 W 3 E R 5 T 6 Y 7 U` (high octave). 12 semitones per row | `1.` ~ `7˙` | Not used |
| Roco Kingdom handpan (洛克王国手碟) | `T Y U` = high do re mi (`1˙` ~ `3˙`); `F G H J K` = middle mi fa sol la si (`3` ~ `7`); `B` = low la (`6.`). Natural notes only | `6.` ~ `3˙` | Not used |
| Roblox piano layout (Roblox 钢琴键位) | One binding per semitone, 61 in total. The white keys are the same four rows (`1..0` = C2..E3, `Q..P` = F3..A4, `A..L` = B4..C6, `Z..M` = D6..C7); each black key is written as "the same key + `Shift`" and shows as `! @ $ % ^ * (` or `Q W E …` on the key cap | `1..` ~ `1˙˙` (C2..C7) | Not used |

"21-key chromatic" and "8-key chromatic" both have function keys on: without a modifier key you get natural notes; hold one to get altered notes
(`Shift`+`Z` raises a semitone, `Ctrl`+`Z` lowers a semitone, or hold the middle mouse button and press `Z`). The two mouse buttons of "8-key chromatic" can also shift the whole row up or down an octave.

"Roblox piano layout" uses no function keys: each of the 61 notes has its own binding, and a black key is its own binding (written as "the white key below + `Shift`").
Key caps follow the sheet notation of the target program: white-key letters show lowercase (`z x c v b n m`), black keys show the shifted character of the same key (`Z X C V B N M`, `! @ $ % ^ * (`).

Ranges in the table use **numbered musical notation (jianpu)**: digits 1..7 are scale degrees, 1 = do. Accidentals are written before the digit, e.g. `#4`.
A `˙` after the digit is one octave higher; `˙˙` is another octave higher.
A `.` after the digit is one octave lower; `..` is another octave lower.

**Notes with no corresponding key are not played.** Whichever semitone is missing from the range is skipped — no pitch substitution. This behavior is fixed; there is no switch for it in the UI.
"Identity V layout" is a full chromatic scale: every semitone has its own key, no octave keys or semitone-up keys needed.
"8-key chromatic" has only natural-note keys; semitones are filled in with the middle mouse button.
"Roblox piano layout" has 61 bindings: 36 white keys plus 25 black keys. E, B, and the top C7 have no black key above them, which is exactly why there are 25.

**Function keys.** At the top of the "Function Keys" (功能键) block there is an "Enable function keys" (启用功能键) checkbox.
When ticked, you can bind four keys: octave up, octave down, semitone up, semitone down. The range expands one octave up and down accordingly,
and the semitone-down key extends the lowest note one more semitone down.
When unticked, the scheme does not use these four keys. Notes outside the layout range are simply not played.

**Tie-break rule for semitone keys.** When the same black key is reachable both as "white key below + semitone up" and "white key above + semitone down",
the semitone-up key is always used. The semitone-down key is used in two places: to fill in black keys when the scheme has no semitone-up key bound, and for the one semitone below the lowest key
(which the semitone-up key cannot reach). "21-key chromatic" is the scheme with both semitone keys bound.

**Editing key bindings.** Click the "Settings…" (设置…) button on the main window and switch to the "Keys" (键位) page. This page has three blocks:

| Block | Purpose |
|---|---|
| Scheme (方案) | Select scheme, New Scheme… (新建方案…), Rename… (改名…), Import Scheme… (导入方案…), Export Scheme… (导出方案…), Manage ▾ (管理) (delete scheme, restore default settings) |
| Key bindings (按键绑定) | Keycap squares arranged in rows. The large text on a block is the jianpu pitch, the small text is the solfège name, the key name is on the lower half, and `✕` at the top right unbinds |
| Function keys (功能键) | The "Enable function keys" checkbox, with three keys below: octave up, octave down, semitone up |

**Row layout.** Keys in a row are ordered by pitch from left to right. A row does not wrap; if it is too long it scrolls horizontally.
The two 21-key schemes form a neat three-row seven-column grid; the 36-key one forms a neat three-row twelve-column grid; the Roblox piano scheme is four rows (a white key and its black key share a row).
The "+" at the end of a row adds a note at the end of that row. The "+ Add a Row" (+ 加一行) below the last row adds a whole row, one octave above the previous row.

**Waiting for a key.** After you click a key block it turns into "Press a key…" (按一个键…); press a keyboard or mouse button to bind it. Press Esc to cancel.
For keys you cannot press on the keycaps, like `PageUp`, click "Choose Key Name" (选键名) next to it and pick from a list.
Pressing and releasing `Shift`, `Ctrl`, or `Alt` alone also binds them as function keys.

**Which notes are still unbound.** A statistics line stays at the bottom of the window, e.g. "21 bindings, 3 rows. Playable from 1 to 7˙˙, of which 0 notes have no key bound. (Function keys off)".

**Range is determined by the keys.** No numbers to fill in. As far as the keys go is as far as you can play. When function keys are off, the octave keys and the semitone-up key do not participate.

**Scheme files.** The built-in schemes cannot be renamed or deleted. To change one, click "New Scheme…" (新建方案…) first to copy it.
Custom schemes live under `%LOCALAPPDATA%\MidiKeyPlayer\schemes\` and remain in the list after a restart. "Import Scheme…" (导入方案…) and "Export Scheme…" (导出方案…) work with a single JSON file, convenient for sharing with each other.

**Per-scheme memory.** Tempo, transposition, and the input compatibility level are saved separately by key layout scheme name.

**Relationship with device live performance.** The base octave for MIDI device input is set separately in the "MIDI Device Live Performance" area.
The octave keys and the semitone-up key follow the current scheme's "Enable function keys".

## FAQ

**No response in the target program, but typing works in Notepad?**

Check four things in order.

1. Click the target window first.
2. Set the target program to borderless windowed.
3. Run both the target program and this program as administrator.
4. Switch the input method to English.

**No dropped notes while standing still, but notes drop once you move?**

This is the keyboard hardware ghosting problem, not a software problem. See item 4 of "Cautions" above.

**Occasional missing notes?**

When playback ends, the message bar at the bottom of the window shows a line of "timing diagnostics" (时序诊断). The full record is written to `%LOCALAPPDATA%\MidiKeyPlayer\play.log`. When you click "Stop" (停止) manually, the message bar is overwritten by "Stopped." (已停止。) — read play.log directly.

- Insufficient modifier lead time: change "Input compatibility" to "Safe" (稳健). That level raises the lead time from 40ms to 70ms.
- Too many same-key re-triggers: switch to the "Safe" level. It raises the same-key re-trigger interval from 45ms to 80ms. Tempo does not change this physical interval.
- The count of notes whose duration was clamped to the floor is not 0: this score is denser than the fastest speed the level allows. Switch to the "Safe" level, or slow down.

**MIDI device not responding?**

Check four things in order.

1. If the device is occupied by other software, close that first (a MIDI input can usually be opened by only one program).
2. Click "Refresh" (刷新) and confirm the device appears in the dropdown.
3. The status text should show "Live performance active" (实时演奏中). If it stays at "Starting…" (启动中…) without moving, the device failed to open; the log has the reason.
4. The notes you play have no corresponding key. Check the status text: if it shows "No corresponding key" (没有对应的键), adjust "Base Octave" (基准八度) or tick "Auto-fit Range" (自动贴合音域).

**What happens when you click the window's ×?**

A full exit. The program stops playback, releases all keys, and unregisters the hotkeys. If you only want to leave the target window briefly, minimize the window instead. The tray menu can control start, pause, stop, and exit.

## Directory Layout

```
MidiKeyPlayer/              # 主程序（Avalonia + .NET 8）
MidiKeyPlayer/docs/更新说明.txt  # 本说明书（打包时嵌进 exe）
MidiKeyPlayer/docs/更新日志.txt  # 累积更新日志（打包时进 zip）
MidiKeyPlayer/build-win.sh  # 发布脚本：打包成单文件 exe 的 zip
MidiKeyPlayer/release/      # 发布产物：zip 与解出的 exe（打包生成）
示例MIDI/                   # 开发用示例曲目（不进发布包）
tools/run-selftest.ps1      # 跑一次内置自检，统计 PASS / FAIL
tools/release.ps1           # 发新版：版本号加一、写日志、构建、验证、发 Release
LICENSE                     # MIT 许可
THIRD-PARTY-NOTICES.md      # 第三方组件许可声明
```

## Self-Test (for Development)

The program ships with a pure-logic self-test covering key layout schemes and jianpu conversion. It creates no window, registers no hotkeys, and touches neither keystrokes nor MIDI devices.

1. Build the Release configuration: `dotnet build MidiKeyPlayer/MidiKeyPlayer.csproj -c Release`.
   The csproj sets `RuntimeIdentifier=win-x64`; the build output is in `MidiKeyPlayer\bin\Release\net8.0\win-x64\`.
2. Run `tools/run-selftest.ps1`. The script finds the exe automatically by priority: `bin\Release\net8.0\win-x64\`, `release\win-x64\`, `bin\Release\net8.0\`.
   Then it sets the environment variable, waits for the program to exit, reads back the report, and counts PASS / FAIL. To specify another exe, use `-ExePath <path>`.
3. Exit code 0 = all passed, 1 = a case failed. See the script header for the script's other exit codes.

Currently all 29 assertions pass, exit code 0. It used to be 36: 13 of them tested a key-name translation copy internal to the self-test;
the copy has been deleted, and those 13 went with it.

You can also manually set `MIDIKEY_GAME_SELFTEST=<report file path>` and then start the program. The program manifest requires administrator privilege, so the self-test process is elevated as well.

## Packaging Yourself (for Development)

Regular users do not need this section. Only install these if you build from source yourself:

- **.NET 8 SDK**: compiles the program.
- **Python 3**: the packaging script uses it to write the zip; the standard library `zipfile` must be available. No usable Python, no package.

The packaging command is `bash MidiKeyPlayer/build-win.sh`; the output is under `MidiKeyPlayer/release/`.

Releasing a new version goes through the release script `tools/release.ps1`. It does all of the following in one run:

1. Bump the patch version by +1 (changeable with `-Bump Minor` / `-Bump Major`).
2. Add a section for the new version at the top of `MidiKeyPlayer/docs/更新日志.txt`.
   Without `-Notes`, it uses the commit titles since the last tag as a draft.
3. Update `<Version>` in `MidiKeyPlayer/MidiKeyPlayer.csproj` and the version number on the first line of `更新说明.txt`.
4. Run `build-win.sh` to produce the package. The zip contains the exe and `更新日志.txt`.
5. Run the built-in self-test; the exit code must be 0.
6. Run UI snapshots of the main window, both Settings pages, and the main window in the dark skin, confirming all of them render.
   The two Settings steps walk through "open → close → open" on their own, verifying along the way that the content ownership moves back and forth cleanly.
7. Run the folder-songs song-switch regression: click three songs in a row; each step must switch over, and the list row count must not collapse.
8. Commit, push main, tag, create the Release, upload the zip. After upload, compare the remote asset's sha256 with the local zip;
   a mismatch is an error.

```
powershell -File tools\release.ps1 -DryRun                     # 只看会发什么
powershell -File tools\release.ps1 -Notes "（这一版改了什么）"   # 自己写日志
powershell -File tools\release.ps1 -SkipPush                   # 只构建验证，不提交不发布
powershell -File tools\release.ps1 -TrimParity                 # 加做裁剪比对
```

Rules: every release ships a new version. Already-published packages are not touched, not overwritten, and not re-uploaded.
The release is fully automatic; the script goes all the way to upload completion without stopping to ask.
If the tag already exists, the script stops immediately. If the changelog has no section for the current version, `build-win.sh` refuses to package.

The release package has trimming enabled; the settings are in `MidiKeyPlayer.csproj`. Trimming brings the exe from 45.6 MB down to 23.0 MB.
Reflection-related assemblies (`MidiKeyPlayer`, the `Avalonia` family, `Melanchall.DryWetMidi`) are pinned with
`TrimmerRootAssembly`; not a single type or member is removed.

After touching trim settings or upgrading dependencies, add `-TrimParity` when releasing. It additionally builds an untrimmed exe
and compares the two UI snapshots byte for byte. If the comparison fails, it does not release.
For finer curve verification (attaching an Avalonia log listener to check binding errors), see `midikey-audit\fixes\58-trim-verify.md`.

## Auto-Update

On startup it checks for a new version in the background (on by default; auto-download supported since v1.0.10). When a new version is found, a banner appears at the top of the window:

- **Left-click the banner**: automatically download the update package with progress shown; after the download completes, click once more — the program exits, overwrites the old files automatically, and restarts into the new version.
- **Click once more while downloading**: cancel the download.
- **Right-click the banner**: skip this version; the next version will still prompt.
- If the download or verification fails, click the banner to retry; when there is no direct update-package link, it falls back to "Open Download Page" (打开下载页) for a manual download.
- If the piano roll has unexported edits before updating, it prompts you to export MIDI first.
- After the update restart, the new version's first launch writes a line "updated from vX to vY" (已从 vX 更新到 vY) to play.log, confirming the replacement succeeded.

Settings → General → About (设置 → 常规 → 关于) shows the current version number, a "Check for Updates" (检查更新) button, and entries to view the release notes / third-party notices / license.
That page also carries the free-software notice and the author entries (Bilibili page, Afdian sponsorship, copy the QQ group number). The sponsor page is Settings → Sponsor (设置 → 赞助).

Update packages are downloaded only from this repository's Releases (address whitelist), and the exe inside the package is verified intact before replacement.
To turn off the check, change `Enabled` in `MidiKeyPlayer/Engine/AutoUpdate.cs` to `false`.

## Settings Migration

On first launch, if the new directory has no settings file, the program tries to read the old directory `%LOCALAPPDATA%\HarpAutoPlayer`
and migrates tempo, transposition, hotkeys, and MIDI device settings. A migration failure only writes to the log and does not interrupt startup.

## Technical Notes

- UI: Avalonia (.NET 8). MIDI parsing and device connection: DryWetMidi.
- Release package: single-file, self-contained, trimming on, reflection-related assemblies pinned from trimming. Zip ~17 MB, exe ~23 MB.
- Third-party component licenses: see `THIRD-PARTY-NOTICES.md`. This text is embedded in the exe.
- Input: Windows SendInput, scancode mode. Global hotkeys: WH_KEYBOARD_LL.
- Preview: the winmm MIDI output built into Windows. No third-party audio library.
- Timing margins are given in **physical milliseconds** and do not scale with playback tempo. Standard level: the modifier key leads the note key by at least 40ms; during file playback each note is held for at least 17.7ms (46ms on the MIDI device live-performance path); same-key re-trigger is at least 45ms apart; the dispatch-ahead amount is also a fixed physical time.
- At 10% and 400%, these physical intervals are exactly the same as at 100% (measured 40ms / 17.7ms). What changes is the musical time they correspond to: at fast playback the same physical interval covers more music, at slow playback less.
- The result is that fast playback no longer drops notes because intervals get squeezed within one frame. The cost is that at slow playback, notes sound slightly later than the score overall, and release slightly later too.
- Notes are spaced with "slots": a note overlapping the previous one is deferred to after it, rather than shortening the previous note to make room. This produces no zero-duration keystrokes.
- There is only one set of piano-roll coordinate conversions. Rendering and hit-testing share it, symmetric in both directions.

## License

This program is under the MIT license. `LICENSE` is at the repository root.
Third-party component licenses: see `THIRD-PARTY-NOTICES.md`.
Both texts are packaged into the exe and distributed with the program. The current version has no UI entry to open them.

## Disclaimer

This tool is for learning and personal use only. Account penalties or other consequences arising from use are borne by the user.
