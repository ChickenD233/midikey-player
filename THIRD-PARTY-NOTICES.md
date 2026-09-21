# 第三方组件许可声明

本文件列出 MidiKeyPlayer 用到的第三方组件与它们的许可。

本文件的正文以 `Docs/THIRD-PARTY-NOTICES.md` 嵌在 `MidiKeyPlayer.exe` 里。
Avalonia 资源地址是 `avares://MidiKeyPlayer/Docs/THIRD-PARTY-NOTICES.md`。
发布包 `MidiKeyPlayer-win-x64-<版本>.zip`（当前 `MidiKeyPlayer-win-x64-1.0.0.zip`）里只有一个 `MidiKeyPlayer.exe`，没有松散文件。

> 本程序**自身**用 MIT 许可，全文见仓库根 `LICENSE`，版权行是 `Copyright (c) 2026 ChickenD233`。
> 该文本也以 `Docs/LICENSE` 嵌在 `MidiKeyPlayer.exe` 里。

本声明、`更新说明.txt` 与 `LICENSE` 都随 exe 分发，但当前版本没有打开它们的界面入口。
要看内容，请打开仓库里的同名源文件。

组件清单来自 `MidiKeyPlayer/MidiKeyPlayer.csproj` 的直接引用，加上 `MidiKeyPlayer/obj/project.assets.json` 里记录的实际还原结果（只读，没有重跑 restore）。

## 1. 直接依赖

| 组件 | 版本 | 许可 | 版权行 |
|---|---|---|---|
| Avalonia | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Desktop | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Themes.Fluent | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Melanchall.DryWetMidi | 7.2.0 | MIT | Copyright © Melanchall 2024 |

## 2. 捆绑的原生组件（嵌在 exe 里）

| 组件 | 版本 | 许可 | 版权行 |
|---|---|---|---|
| IbInputSimulator | v0.4.1 | MIT | Copyright (c) 2021 Chaoses-Ib |

`IbInputSimulator.dll`（约 239 KB，x64）作为 Avalonia 资源嵌在 `MidiKeyPlayer.exe` 里
（`avares://MidiKeyPlayer/Libs/IbInputSimulator.dll`），首次使用「罗技 G HUB 驱动」输入方式时
释放到 `%LOCALAPPDATA%\MidiKeyPlayer\` 再加载。来源：
https://github.com/Chaoses-Ib/IbInputSimulator/releases 。

## 3. General MIDI 音色表（声部识别）

左侧列表的「声部」列用 General MIDI 1（MIDI 厂商协会，1991 年）的 128 个音色名。
本程序只用了这份规范的音色名与音色号对应关系：`MidiKeyPlayer/Midi/GmInstrument.cs`
里那张 128 条的中文音色表，加上按音色号分组的角色归类（鼓 / 贝斯 / 吉他 / 弦乐 / 人声…）。
表与归类是本仓库自己写的，不含任何第三方代码。General MIDI 是商标，此处只按规范做兼容命名。

## 4. 传递依赖

还原结果里还有下列组件。Windows x64 单文件产物会带上其中的托管程序集与原生库。
构建期工具 `Microsoft.NET.ILLink.Tasks 8.0.31` 不进包，故不列。

| 组件 | 版本 | 许可 | 版权行 |
|---|---|---|---|
| Avalonia.BuildServices | 11.3.2 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.FreeDesktop | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Native | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Remote.Protocol | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Skia | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Win32 | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.X11 | 11.3.20 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| HarfBuzzSharp | 8.3.1.1 | MIT | Copyright (c) 2015-2016 Xamarin, Inc.；Copyright (c) 2017-2018 Microsoft Corporation |
| HarfBuzzSharp.NativeAssets.Linux / macOS / WebAssembly / Win32 | 8.3.1.1 | MIT | 同上 |
| MicroCom.Runtime | 0.11.0 | MIT | Copyright 2021 © Nikita Tsukanov |
| SkiaSharp | 2.88.9 | MIT | Copyright (c) 2015-2016 Xamarin, Inc.；Copyright (c) 2017-2018 Microsoft Corporation |
| SkiaSharp.NativeAssets.Linux / macOS / WebAssembly / Win32 | 2.88.9 | MIT | 同上 |
| System.IO.Pipelines | 8.0.0 | MIT | © Microsoft Corporation. All rights reserved. |
| Tmds.DBus.Protocol | 0.21.3 | MIT | Copyright Tom Deseyn |
| Avalonia.Angle.Windows.Natives（ANGLE） | 2.1.25547.20250602 | BSD 3-Clause | Copyright 2018 The ANGLE Project Authors |

## 5. .NET 8 运行时（随 exe 分发）

发布 exe 是自包含单文件（`MidiKeyPlayer.csproj` 里 `SelfContained` + `PublishSingleFile`）。
.NET 8 运行时已内嵌在这个 exe 里，并随包分发。

运行时本体用 MIT 许可。版权行是 `Copyright (c) .NET Foundation and Contributors`。
许可全文在运行时包的 `LICENSE.TXT` 里，本机路径：
`.tools\nuget\microsoft.netcore.app.runtime.win-x64\8.0.31\LICENSE.TXT`。
官方地址：https://github.com/dotnet/runtime/blob/main/LICENSE.TXT 。

运行时还带一批第三方组件。完整声明在运行时包的 `THIRD-PARTY-NOTICES.TXT` 里，本机路径：
`.tools\nuget\microsoft.netcore.app.runtime.win-x64\8.0.31\THIRD-PARTY-NOTICES.TXT`（1272 行）。
这份文件逐条列出各组件，包括 ASP.NET（Apache-2.0）、Slicing-by-8（BSD）、Unicode 数据许可、
Zlib、Mono（MIT）、W3C 文档许可、LLVM（Apache-2.0 with LLVM Exceptions）等。
同一份清单也在 .NET 官方仓库里：https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT 。
清单内容随版本变化。核对时用本机运行时包里的原文。

本文件不逐条抄录这份清单。该清单也没有嵌进 exe。

## 6. MIT 许可全文（适用于第 1、2、4、5 节里标 MIT 的组件）

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

每个 MIT 组件的版权行见第 1、2、4 节表格。

## 7. BSD 3-Clause 许可全文（ANGLE）

```
// Copyright 2018 The ANGLE Project Authors.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
//     Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//
//     Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//
//     Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
//     Ltd., nor the names of their contributors may be used to endorse
//     or promote products derived from this software without specific
//     prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
// COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
// LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
```

许可原文取自本机 NuGet 缓存里的包内文件：`skiasharp\2.88.9\LICENSE.txt`、`harfbuzzsharp\8.3.1.1\LICENSE.txt`。
多数包的 `.nuspec` 写 `<license type="expression">MIT</license>`，版权行取 `<copyright>` 字段。
`Avalonia.Angle.Windows.Natives` 是例外：它的 `.nuspec` 写 `<license type="file">LICENSE</license>`，
`<copyright>` 是 `Copyright 2013-2025 © The AvaloniaUI Project`。
ANGLE 的许可全文与版权行 `Copyright 2018 The ANGLE Project Authors` 取自包内 `LICENSE` 文件。
