# 第三方组件声明

CloudFlow 本身采用 GPL-3.0（见根目录 `LICENSE`）。以下第三方组件随源码一并分发，各自保留其原始许可证；
NuGet 依赖（见各项目 `.csproj` 的 `PackageReference`）由 NuGet 在构建时拉取，未随源码提交，其许可证信息见各自的官方仓库。

## xterm.js（及附加组件）

路径：`src/CloudFlow.Terminal/Terminal/Assets/xterm.js`、`xterm.css`、`addon-fit.js`、`addon-search.js`、`addon-webgl.js`

这些文件是 [xterm.js](https://github.com/xtermjs/xterm.js) 项目的构建产物（压缩后的浏览器分发包），随
CloudFlow 一并分发用于底部终端面板（xterm.js 官方分发的压缩文件不含逐文件许可证头，
本声明按 MIT 要求随附完整许可证文本）。

```
Copyright (c) 2014 The xterm.js authors. All rights reserved.
Copyright (c) 2012-2013, Christopher Jeffrey (MIT License)
https://github.com/chjj/term.js

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```
