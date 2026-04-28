# 修正内容の確認 (Walkthrough)

## 実施内容
- `https://github.com/Mono-Gen/ai-rules` から最新のAIルール一式を取得しました。
- プロジェクトルートに `.agents` フォルダを展開し、`config.md` にて以下のルールを有効化しました：
  - global_rules.md
  - code_style_guide.md
  - device_control_rules.md
  - ui_ux_rules.md
  - resource_management_rules.md
  - documentation_rules.md
- `.gitignore` に `.agents/*` および `.docs/*` が含まれていることを確認しました。

## 検証結果
- [x] `.agents/config.md` の記述が正しく更新されている。
- [x] 必要ファイルがすべて配置されている。
