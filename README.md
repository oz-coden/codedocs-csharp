# CodeDocs.CSharp

C# の `.sln` / `.csproj` を Roslyn・MSBuild で静的解析し、API ドキュメントを生成する CLI ツールです。HTML、複数ファイルの Markdown、単一ファイルの Markdown、JSON に対応します。

> Document what the compiler can know, not what the tool can guess.

コンパイラが把握できる型・メンバ・シグネチャ・構造的関係・XML Documentation を記録します。AI や LLM、説明文の自動生成、メソッド内部の呼出し解析は使用しません。

## 必要な環境

- ツールのビルド・実行: .NET 9 SDK
- 解析対象のビルドに必要な SDK・ワークロード・参照アセンブリ
- HTML の閲覧: 最新のブラウザ

Roslyn 4.14 を使用し、C# 13 の宣言を解析できます。解析前に対象プロジェクトの依存関係を `dotnet restore` で復元してください。MSBuild はプロジェクトの設定を評価するため、信頼できるプロジェクトを入力してください。

## ビルドとインストール

```bash
git clone https://github.com/oz-coden/codedocs-csharp.git
cd codedocs-csharp
dotnet build CodeDocs.CSharp.sln -c Release
dotnet run --project tests/CodeDocs.Tests -c Release --no-build
dotnet pack src/CodeDocs.Cli -c Release --no-build -o artifacts/packages
dotnet tool install --global CodeDocs.CSharp --version 0.1.0 --add-source ./artifacts/packages
codedocs --help
```

NuGet.org への公開は前提としていません。上記はローカルで作成したパッケージをインストールする手順です。既にインストール済みなら、`dotnet tool update --global` を使用できます。

## 使用例

```bash
# プロジェクトの依存関係を復元して HTML を生成
dotnet restore MyProject.csproj
codedocs build MyProject.csproj -o docs/api

# ソリューション全体の単一 Markdown
codedocs build MySolution.sln --format markdown --single-file -o docs-md

# 複数の形式を一度に生成
codedocs build MySolution.sln --format html --format markdown --format json -o docs/api

# protected API も表示
codedocs build MyProject.csproj --visibility api
```

インストールせずに実行する場合:

```bash
dotnet restore samples/Example/Example.csproj
dotnet run --project src/CodeDocs.Cli -- build samples/Example/Example.csproj -o artifacts/example
```

入力を省略すると、カレントディレクトリ直下の `.sln` / `.csproj` を検索します。候補が 1 件のときだけ自動選択します。0 件または複数ならエラーになり、親ディレクトリは検索しません。

## オプション

| オプション | 意味 |
| --- | --- |
| `-o`, `--output <directory>` | 出力先。既定は `docs/api` |
| `--format <html\|markdown\|json>` | 出力形式。複数回指定可能。既定は HTML |
| `--visibility <public\|api\|non-private\|all>` | 出力する可視性。既定は `public` |
| `--single-file` | Markdown を `api.md` 1 ファイルに出力 |
| `--on-incomplete <keep-existing\|overwrite\|fail>` | 不完全な Compilation の扱い |
| `--clean` | CodeDocs が管理していない既存の出力ディレクトリも置換 |
| `--no-search` | HTML の検索を無効化 |
| `--verbose` | 警告を含む診断の詳細を表示 |
| `--help`, `--version` | ヘルプ、バージョン |

`--single-file` には Markdown 形式の指定が必要です。出力は常に完全なスナップショットで、削除済みの型・メンバの古いファイルを残しません。`--clean` がなくても、以前 CodeDocs が生成したディレクトリは置換されます。

出力先には専用ディレクトリを指定してください。`.codedocs-output.json` は生成先の管理情報です。入力ファイルのあるディレクトリやその祖先、ソースファイルを含むディレクトリ、シンボリックリンク経由の出力先は置換しません。

### 可視性

| 値 | 対象 |
| --- | --- |
| `public` | 外部アセンブリから実効的に public な API |
| `api` | public と、protected / protected internal の継承者向け API |
| `non-private` | internal、private protected を含み、private を除外 |
| `all` | private、file ローカル型も含む |

包含型の可視性も考慮します。`internal class` 内の `public` メンバは `public` モードには現れません。可視なインターフェイスの明示的実装は独立カテゴリに表示します。

## 設定ファイル

カレントディレクトリの `codedocs.json` を読み込みます。

```json
{
  "input": "./MySolution.sln",
  "output": "./docs/api",
  "formats": ["html", "markdown"],
  "visibility": "public",
  "analysis": { "onIncomplete": "keep-existing" },
  "html": { "search": true },
  "markdown": { "singleFile": false },
  "source": { "showDeclarationFiles": true }
}
```

優先順位は「組込みの既定値 < 設定ファイル < CLI 引数」です。CLI の `--format` は設定ファイルの形式一覧を置き換えます。設定内の相対パスもカレントディレクトリ基準です。未知の設定キーや不正な値はエラーになります。

## 出力

単一形式なら指定した出力先に直接生成します。複数形式なら `html/`、`markdown/`、`json/` に分かれます。

- HTML: `index.html`、`projects/`、`namespaces/`、`types/`、`assets/`、`search-index.json`
- Markdown: `README.md`、`namespaces/`、`types/`
- 単一 Markdown: `api.md`
- JSON: `api.json`

HTML は型ごとに 1 ページを持ち、メンバ一覧と詳細を分離します。検索対象は名前空間・型・メンバ・XML summary です。検索用データを JavaScript としても同梱するため、静的ホスティングに加え `index.html` を直接開いた場合も検索できます。CDN や外部スクリプトへの依存はありません。

Markdown のシグネチャは C# のコードブロックで表示し、その直後に内部参照へのリンクを表示します。単一ファイルでも明示的なアンカーで型・メンバ間のリンクを維持します。

## 解析対象

class / struct / readonly・ref struct / record / record struct / interface / enum / delegate / nested type と、constructor・primary constructor・method・property・indexer・field・event・operator・conversion・明示的インターフェイス実装を扱います。

nullable、dynamic、名前付き tuple、ポインタ、ref / in / out、params、既定値、init、accessor の可視性、ジェネリック制約・変性、属性を保持します。partial 宣言は型として統合し、宣言元の情報も残します。Compilation に含まれる生成コードや record の生成 API を暗黙に除外しません。

内部リンクは解析対象に含まれるシンボルだけに生成します。BCL や NuGet の型は表示しますが、外部 URL を推測しません。継承メンバは元のメンバへの参照として表示します。

XML Documentation は summary / remarks / param / typeparam / returns / exception / example / value / see / seealso / inheritdoc と、code / c / para / list を扱います。明示的な cref、override、インターフェイス実装から `inheritdoc` を解決し、XPath `path` による部分選択にも対応します。継承した param / typeparam とそのインライン参照は、引数の位置に従って継承先の名前へ対応付けます。ローカルの説明を優先し、不正な XPath・空の選択・循環は警告として元の記述を保持します。表形式の XML list は HTML / Markdown の表へ出力します。コメントがなければ説明文を生成しません。

## 不完全な解析と出力保護

| 状態 | 意味 |
| --- | --- |
| Success | Compilation にエラーなし |
| Partial | Compilation は取得できたがコンパイルエラーあり |
| Fatal | プロジェクトのロード失敗など |

Partial の既定ポリシー `keep-existing` は診断を表示し、既存出力を変更しません。`overwrite` は警告付きで生成し、`fail` は生成失敗として扱います。Fatal ではポリシーに関係なく正式出力を変更しません。

生成先と同じ親ディレクトリに一時成果物を作り、検証後にディレクトリを置換します。置換中の失敗では元のディレクトリを復元します。出力先ごとのロックで同時更新を防ぎます。ロックファイルは出力先の隣に残ります。2 回のディレクトリ移動の間には短い非存在期間があり、電源断を含む完全な原子性は保証しません。

| 終了コード | 意味 |
| --- | --- |
| 0 | 完全成功 |
| 1 | 致命的なロード・実行・入出力エラー |
| 2 | 不完全な解析。overwrite による生成後も 2 |
| 3 | CLI 引数・設定の誤り |

## 開発

```bash
dotnet build CodeDocs.CSharp.sln -c Release
dotnet run --project tests/CodeDocs.Tests -c Release --no-build
```

テストランナーは解析、可視性、識別子、XML、出力の決定性、内部リンク、安全な置換、設定、MSBuild 読込、不完全な解析のポリシーを検証します。プロセス間通信を利用できない環境では `--unit-only` をテストランナーに指定し、MSBuild 統合テストだけを明示的に除外できます。CI は Linux・Windows の両方で全テストを実行します。

JSON には表示文字列に加え、属性引数の型付き定数・配列・typeof、tuple 要素名、配列の次元、ポインタと関数ポインタの構造、型引数の識別情報、ジェネリック制約、XML ノードと継承元情報を保持します。delegate の引数・戻り値、accessor の引数、アセンブリ・モジュールの属性も対象です。

設計と現在の制約は [architecture](docs/architecture.md) と [limitations](docs/limitations.md) を参照してください。
