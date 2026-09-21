# 仓库规则

这些规则给维护者和 AI 助手用。发版流程必须按这个来。

## 发版规则：每次发新版本

- 每次发 release 一律发新版本。版本号自动加一（默认补丁号 +1）。
- 已经发布的包不动、不覆盖、不重传。tag 已存在就停手。
- 发版全自动：构建、自检、主窗与设置窗两页的界面快照（外加一张深色皮肤的主窗）、换歌回归、
  提交、推 main、打 tag、建 Release、上传 zip 一次做完。不要问用户「要不要发」，也不要停在上传之前。
  用户改口之前，这就是默认动作。
- 上传后自动校验：比对远端资产的 sha256 与本地 zip。不一致就报错。
- 发版脚本会跑文件夹曲目卡换歌回归（连续点三首，列表不能塌）。这条回归对应的 bug 是
  「选了一首之后别的点不动」，改 `FolderList_SelectionChanged` 或 `RefreshFolderUi` 之后必须跑。
- 设置窗口的「常规」页内容住在 MainWindow.axaml 的 AdvancedStash 里，开窗搬进去、关窗搬回来。
  动 `SettingsWindow` 或那段 XAML 之后，必须跑设置窗快照（它自带开→关→再开）。
- 发版走脚本：`powershell -File tools\release.ps1`。
- 想先看会发什么：`powershell -File tools\release.ps1 -DryRun`。
- 想只构建不发布：`powershell -File tools\release.ps1 -SkipPush`。
- 想强制更新（装旧版的人必须先更新才能继续用）：加 `-Mandatory`，Release 说明里会写 `[强制更新]`。
  只有带强制更新代码的版本（v1.0.30 起）会认这个标记；更老的版本里没有这段逻辑，挡不住。
- 免费声明与作者入口写在 `Engine\AutoUpdate.cs` 的常量里（`FreeNotice` / `AuthorSpaceUrl` /
  `QqGroupNumber` / `AuthorName`）。改文案时把 `NoticeVersion` 一起加一，界面会再弹一次声明。
  第一次使用的验证题只弹一次，答对写进设置的 `AuthorQuizPassed`，不要改成每版都弹。
- 动过裁剪设置或升级依赖之后，加 `-TrimParity`，比对裁剪版与不裁剪版的快照。

## 更新日志必须有历史记录

- 累积日志在 `MidiKeyPlayer/docs/更新日志.txt`。版本从新到旧排。
- 每次发新版，只在最上面加一节。旧版本的记录不删、不改、不覆盖。
- 脚本会用上个 tag 以来的提交标题当日志草稿。措辞要更好，就自己写一份给 `-Notes`。
- 说明：`docs/更新说明.txt` 是当前版本的功能说明，不是日志。两份文件不要合并。
- 发布包（zip）里必须带 `更新日志.txt`，内容与源文件一致。
- `build-win.sh` 会核对：日志里没有当前版本号那一节，就拒绝打包。

## 手工发版步骤（脚本不能跑时照这个来）

1. 改 `MidiKeyPlayer/MidiKeyPlayer.csproj` 里的 `<Version>`，补丁号加一。
2. 在 `MidiKeyPlayer/docs/更新日志.txt` 最上面加当前版本一节。
3. 改 `MidiKeyPlayer/docs/更新说明.txt` 第一行的版本号。
4. 跑 `bash MidiKeyPlayer/build-win.sh`。产物在 `MidiKeyPlayer/release/` 下。
5. 跑 `tools/run-selftest.ps1 -ExePath <新 exe>`。退出码必须是 0。
6. 跑一次界面快照，确认界面正常（做法见 README 的「自己打包」）。
7. 提交并推送 main。打 tag `v<版本>`，发 Release，上传 zip。

## 发布包

- 只放两样东西：`MidiKeyPlayer.exe` 与 `更新日志.txt`。示例曲目不进包。
- 开裁剪（`PublishTrimmed`）。反射相关的程序集用 `TrimmerRootAssembly` 钉住：
  `MidiKeyPlayer`、`Avalonia` 系列、`Melanchall.DryWetMidi`。
  动裁剪设置或升级依赖之后，必须重跑自检与界面快照比对。
- 体积基线：exe 23.2 MB，zip 17.3 MB。明显变大就查原因。
  （v1.1.8 起移除了音频转 MIDI 与 ONNX Runtime 原生库，回到 v1.1.6 的量级。
   v1.1.7 的基线是 exe 26.6 MB、zip 21.2 MB。）

## 其它

- 这个程序会发送模拟按键。文档与界面不写具体游戏名或软件名。
