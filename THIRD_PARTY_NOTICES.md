# Third-party notices

TransMessage does not assign a license to the main repository. The following notices apply to bundled third-party components and development dependencies. No SF fonts, SF Symbols, QQ SDK runtime, or Codex source code is bundled.

| Component | Version | License | Source / bundled notice |
|---|---|---|---|
| .NET runtime, ProtectedData | .NET 10 / 10.0.12 | MIT and upstream third-party notices | [dotnet/runtime](https://github.com/dotnet/runtime), `licenses/dotnet-MIT.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` |
| WPF / Windows Forms | .NET 10 | MIT and upstream notices | [dotnet/wpf](https://github.com/dotnet/wpf), [dotnet/winforms](https://github.com/dotnet/winforms), `licenses/wpf-MIT.txt`, `licenses/wpf-THIRD-PARTY-NOTICES.txt`, `licenses/winforms-MIT.txt`, `licenses/winforms-THIRD-PARTY-NOTICES.txt` |
| ASP.NET Core | .NET 10 | MIT and upstream notices | [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore), `licenses/aspnetcore-MIT.txt`, `licenses/aspnetcore-THIRD-PARTY-NOTICES.txt` |
| System.CommandLine | 2.0.12 | MIT | [dotnet/command-line-api](https://github.com/dotnet/command-line-api), `licenses/System.CommandLine-MIT.txt` |
| Tomlyn | 2.10.1 | BSD-2-Clause | [xoofx/Tomlyn](https://github.com/xoofx/Tomlyn), `licenses/tomlyn-BSD-2-Clause.txt` |
| Serilog | 4.4.0 | Apache-2.0 | [serilog/serilog](https://github.com/serilog/serilog), `licenses/serilog-Apache-2.0.txt` |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | [serilog/serilog-sinks-file](https://github.com/serilog/serilog-sinks-file), `licenses/serilog-sinks-file-Apache-2.0.txt` |
| xUnit / adapter | 2.9.3 / 4.0.0 | Apache-2.0 | [xunit/xunit](https://github.com/xunit/xunit), development/test only |
| Microsoft.NET.Test.Sdk / TestPlatform / CodeCoverage | 18.10.1 | MIT | [microsoft/vstest](https://github.com/microsoft/vstest), development/test only |

Microsoft fonts and icon glyphs used by the UI are supplied by Windows, not redistributed font files. The simple application icon is drawn by this repository's own icon script.

The QQ Node/Python SDKs and Codex were studied as protocol references; no runtime dependencies or large source blocks from those repositories were copied. See `docs/RESEARCH.md` for exact commits.

The complete resolved package graph, including development transitives, is in each project's `packages.lock.json`. Runtime-dependent upstream notices are included with the self-contained binary. Original license texts have been retained unchanged.
