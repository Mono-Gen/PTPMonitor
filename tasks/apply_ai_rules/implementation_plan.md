# AIルールの適用

https://github.com/Mono-Gen/ai-rules からルールを取得し、現在のプロジェクトに適用する。

## Proposed Changes

### [NEW] .agents/
- ルール設定ファイル群をプロジェクトルートに配置。

### [MODIFY] .agents/config.md
- すべてのルールファイルを有効化。

### [MODIFY] .gitignore
- `.agents/*`, `.docs/*` が除外されていることを確認（既存）。

## Verification Plan
- `.agents` フォルダが存在することを確認。
- `config.md` で各ルールがコメントアウトされていないことを確認。
