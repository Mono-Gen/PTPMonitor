# PTPMonitor GitHubリリース作成計画 (v1.6.7)

PTPMonitor v1.6.7 を GitHub のリリース（Releases）として公開します。
GitHub CLI (`gh.exe`) が利用可能なことが確認できたため、これを用いて自動でリリース作成とバイナリのアップロードを行います。

## ユーザーレビューが必要な事項
> [!IMPORTANT]
> GitHub CLI (`gh.exe`) を用いて、`v1.6.7` のリリース作成と `PTPMonitor_v1.6.7.zip` のアップロードを一括で行います。
> 実行前に、リリースノートの内容に問題がないかご確認ください。

## 実施済みの内容
- ローカルの修正内容とバージョンの確認。
- Git タグ `v1.6.7` の作成。
- リモートへのタグ `v1.6.7` のプッシュ。

## 提案する変更 / 実施手順

### 1. GitHub CLI を用いたリリース作成
以下のコマンドを実行します：
```powershell
& 'C:\Program Files\GitHub CLI\gh.exe' release create v1.6.7 `
    --title "PTPMonitor v1.6.7" `
    --notes-file RELEASE_NOTES.md `
    PTPMonitor_v1.6.7.zip
```

### 2. 事前準備
- `CHANGELOG.md` から今回のバージョン（v1.6.7）の情報を抽出し、一時ファイル `RELEASE_NOTES.md` を作成します。
- リリース成功後、一時ファイルを削除します。

## オープンな質問
- [ ] リリースノートに含めるべき特記事項はありますか？（現状は CHANGELOG.md に準拠します）
- [ ] 今後、GitHub Actions を使用した自動リリース（タグを打ったら自動で zip 作成して Release に上げる）を構築したいですか？

## 検証プラン
- [x] タグが GitHub 上に存在することを確認。
- [x] `PTPMonitor_v1.6.7.zip` の中身が正しい（.exe, config, manuals が含まれている）ことを確認済み。
