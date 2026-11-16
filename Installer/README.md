# Windows インストーラー

`ThumbnailExplorer-Setup-x64.exe` を実行します。利用規約への同意にチェックすると「次へ」が有効になり、新規または空のフォルダーにインストールできます。既存のインストールを更新する場合は、そのインストール先を選択してください。既定のインストール先は `%LOCALAPPDATA%\Programs\Thumbnail Explorer` です。

アプリの起動には [.NET 10 Desktop Runtime（Windows x64）](https://dotnet.microsoft.com/download/dotnet/10.0) が必要です。セットアップは現在の Windows ユーザーにインストールします。

Windows の「設定 → アプリ → インストールされているアプリ」から Thumbnail Explorer をアンインストールできます。インストール先にある `Uninstall.exe` も使用できます。利用者のファイルと、`%LOCALAPPDATA%\ThumbnailExplorer` に保存された設定・サムネイルは残ります。

## ビルド

.NET 10 SDK と [NSIS](https://nsis.sourceforge.io/Download) が必要です。リポジトリのルートで実行します。

```powershell
.\Installer\Build.ps1 -MakeNsis 'C:\Program Files (x86)\NSIS\makensis.exe'
```

成果物は `Release/ThumbnailExplorer-Setup-x64.exe` です。ビルドには `Terms.ja.txt` と `Terms.en.txt` を使用します。
