# Third-party notices

Windshot includes the following third-party material.

## Fluent System Icons

Toolbar icons and stickers are path data extracted from Fluent System Icons
(<https://github.com/microsoft/fluentui-system-icons>) by `tools/make-fluent-icons.ps1`, and the
hand and rotate cursors (`src/Windshot/Assets/*.cur`) are drawn from its glyphs by
`tools/make-cursors.ps1`.

```
MIT License

Copyright (c) 2020 Microsoft Corporation

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

## Runtime libraries

The app ships with these libraries (as NuGet packages, built into the self-contained app), each
under the MIT License, whose text is above:

- **Windows App SDK and WinUI 3** (<https://github.com/microsoft/WindowsAppSDK>,
  <https://github.com/microsoft/microsoft-ui-xaml>): Copyright (c) Microsoft Corporation.
- **Win2D** (<https://github.com/microsoft/Win2D>): Copyright (c) Microsoft Corporation.
- **.NET runtime and libraries, including Windows Forms** (<https://github.com/dotnet/runtime>,
  <https://github.com/dotnet/winforms>): Copyright (c) .NET Foundation and Contributors.

## Microsoft Store badge

`site/badges` holds Microsoft's official "Get it from Microsoft" badge, used on the website under
Microsoft's badge guidelines. It's Microsoft's trademark and isn't covered by this project's license.
