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

## 3. 内置的音频转 MIDI 模型与算法（basic-pitch）

「从音频转 MIDI」用 Spotify 的 basic-pitch 模型。模型文件以嵌入资源进 exe，不落松散文件。

| 组件 | 版本 | 许可 | 版权行 |
|---|---|---|---|
| basic-pitch 模型（ICASSP 2022，`nmp.onnx`，230 KB） | 0.4.0 的模型 | Apache-2.0 | Copyright 2022 Spotify AB |
| basic-pitch 音符解码算法（`note_creation.py` 的移植） | 0.4.0 | Apache-2.0 | Copyright 2022 Spotify AB |

来源：https://github.com/spotify/basic-pitch 。

本程序里的对应实现：

- `MidiKeyPlayer/Assets/nmp.onnx` — 模型本体，原封不动的官方导出。
  嵌入名 `MidiKeyPlayer.Assets.nmp.onnx`（见 `Audio\BasicPitch.cs`）。
- `MidiKeyPlayer/Audio/Onnx/` — 自写的极简 ONNX 执行器，只实现这个模型用到的 23 个算子，
  不含任何 basic-pitch 源码。
- `MidiKeyPlayer/Audio/NoteDecoder.cs` — 把 `basic_pitch/note_creation.py` 的
  `output_to_notes_polyphonic` 改写成 C#。相对上游删掉了弯音（pitch bend）一支：
  按键播放器发不出弯音，卷帘也不显示它。

推理参考实现（上游口径）与逐窗口数值比对脚本不在发布包里，只在开发机上用。

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

## 8. Apache-2.0 许可全文（basic-pitch）

```
Copyright 2022 Spotify AB

                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

许可原文取自 basic-pitch 仓库根 `LICENSE`（https://github.com/spotify/basic-pitch/blob/main/LICENSE ），
版权行是 `Copyright 2022 Spotify AB`。
