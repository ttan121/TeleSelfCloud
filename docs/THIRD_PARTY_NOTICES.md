# Third-party notices

This file records the third-party components included in the Windows x64 desktop publish output. Keep the matching text files under `licenses/` beside the application when distributing it.

| Component | Version | License | Notice or source |
| --- | --- | --- | --- |
| TDLib native Windows package | 1.8.67 | `BSL-1.0 AND Apache-2.0 AND Zlib` | `licenses/BSL-1.0.txt`, `licenses/Apache-2.0.txt`, and `licenses/Zlib.txt`; [ForNeVeR/tdlib.native](https://github.com/ForNeVeR/tdlib.native), repository commit `375f593d6b3602fa723caf2d83cf3ba250af97ca` |
| Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | `licenses/MIT.txt`; copyright Microsoft |
| SQLitePCLRaw.core | 3.0.2 | Apache-2.0 | `licenses/Apache-2.0.txt`; copyright 2014–2025 SourceGear, LLC; [upstream](https://github.com/ericsink/SQLitePCL.raw), commit `ca83fcd795385c6ac99b0f9680d000f27f35c1b4`. SQLite itself is public-domain software. |
| SQLite3MC.PCLRaw.bundle/lib/provider; SQLite3 Multiple Ciphers | 2.4.0 (SQLite 3.53.4) | MIT | `licenses/SQLITE3MC-MIT.txt`; [upstream NuGet repository](https://github.com/utelle/SQLite3MultipleCiphers-NuGet), commit `78ba505f8e52e05fb819bac7f52780784382d5d9` |
| Microsoft .NET and Windows Desktop runtimes | 10.0.12 | MIT | `licenses/MIT.txt`, `licenses/WINDOWS-DESKTOP-RUNTIME-LICENSE.txt`, and `licenses/DOTNET-THIRD-PARTY-NOTICES.txt`; copyright notices are retained in the runtime notice file |

The TDLib package supplies `tdjson.dll`, `z.dll`, `libcrypto-3-x64.dll`, and `libssl-3-x64.dll`. SQLite3MC.PCLRaw.lib supplies `sqlite3mc.dll`. The TDLib package's nuspec declares the combined license expression above, while its archive contains only the BSL text; this project carries all three license texts in the publish output. The default SQLite bundle is no longer included; application database encryption still requires the separate key/migration workflow, which is opt-in per catalog in Desktop Settings and completes at the next startup after a verified recovery-key backup.

TeleDrive was reviewed as a behavioral/design reference. Its root LICENSE is Apache-2.0; its source files were not copied into TeleSelfCloud.

The TDLib package documents a Microsoft Visual C++ Redistributable 2019-or-newer runtime requirement. The self-contained .NET publish does not install that system prerequisite. Run a clean-machine startup check and refresh this inventory whenever package or runtime versions change.
