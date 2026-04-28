# Gitメタデータの修正計画

Gitのコミットメールアドレスが個人のものになっているため、プロジェクトの規定（`global_rules.md`）に従って修正します。

## 修正内容
1.  **Git ローカル設定の更新**:
    - `user.name` を `Mono-Gen` に設定。
    - `user.email` を `mono-gen@users.noreply.github.com` に設定。
2.  **全履歴の修正**:
    - `git rebase --root --exec "git commit --amend --reset-author --no-edit"` を実行し、過去のすべてのコミットのAuthor情報を更新する。

## 検証計画
- `git log -1` を実行し、Author情報が規定通りになっていることを確認する。
