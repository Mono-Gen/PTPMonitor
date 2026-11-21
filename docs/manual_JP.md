# PTPMonitor 使用説明書 (v1.13.0)

PTPMonitor は、ネットワーク上の PTP (Precision Time Protocol) v1 / v2 通信をキャプチャし、デバイスの稼働状況や Boundary Clock (BC) の動作、トポロジーを可視化・診断する「運用特化型プロトコルアナライザー」です。

## 動作環境
- Windows OS (.NET Framework 4.0 以上)
- 管理者権限（パケットキャプチャのために必須）

## セットアップと起動
1. `PTPMonitor.exe` を「管理者として実行」します。
2. 監視対象のネットワークアダプタ番号を選択します。
3. Web UI 用のポートを指定します（デフォルト: 8080）。
4. ブラウザで `http://localhost:8080/` にアクセスします。

## 主要診断機能
### 1. 高精度トポロジー表示と間隔計測
`Delay_Resp` パケットの解析に基づき、どの Follower がどの Leader に同期しているかを正確にツリー表示します。また、Follower が送信する `Delay_Req` の受信間隔をリアルタイム計測し、表示します。

### 2. インテリジェントな競合検知 (BMCA 猶予期間)
競合とは **同一ドメイン内に異なる Grandmaster が複数存在する状態** を指します。Announce (v2) / Sync (v1) から読み取った GM ID が異なる Leader が対象です。同じ GM を報告する Leader が複数いる場合（GM とその配下の Boundary Clock など）は正常であり、競合とは判定しません。GM をまだ読み取れていない Leader（`(own ID, awaiting GM report)` と表示）は、読み取れるまで判定対象から除外されます。深刻度は継続時間で判定します。
- **BMCA (Negotiating) [黄色]**: 複数の GM を検知してから **10秒以内**。マスター切り替えに伴う正常な交渉プロセスとして表示されます。
- **CONFLICT (Persistent) [赤色]**: 競合が **10秒以上** 継続している状態。ネットワーク上の設定ミスや二重マスター障害として、関与する GM ID とともにログ (`[CONFLICT_ALERT]`) に記録されます。

### 2b. GM 変更履歴
各ドメイン見出しの下に、折りたたみ式の `GM changes (N) — last settled GM: ...` を表示します。直近20件の Grandmaster 変更（時刻・旧GM → 新GM）を確認できます。ドメイン内のオンラインな Leader が、検証済みの単一の GM に落ち着き、それが以前に落ち着いた GM と異なる場合に1件記録します。そのため、これまで GM でなかった機器への交代も記録され、GM と配下の Boundary Clock（同じ GM を報告）は1件にまとまります。複数の GM が競合している間は記録しません。検知は1秒周期です。旧 GM が降格した場合（Delay_Req を送り始めるなど）は約1秒以内に反映されますが、旧 GM が単に無通信になった場合はオフライン判定時間（`OfflineTimeoutSeconds`、既定10秒）に最大1秒を加えた時間がかかります。表示される GM は「最後に確定した GM」で、ドメインに Leader がいない間や GM が競合している間も保持されます。追跡するドメインは最大64個で、新しいドメインは現在動作していないドメインの枠にだけ入れ替わります（64個すべてが動作中なら新しいドメインは対象外）。一覧の開閉状態は、ダッシュボードの自動更新後も保持されます。`↻ Network Clear` で消去されます。機器がオフラインになると GM の検証状態は破棄されるため、復帰後に Sync のみを送る機器は、GM を再度 Announce するまで競合判定の対象外です。

### 3. GM 不一致アラート (Diagnostic)
Follower が親（Leader/BC）と異なる Grandmaster ID を受信している場合に、WebUI で赤色警告 (`⚠️ GM Mismatch!`) を出し、トポロジーの乱れを即座に通知します。この判定は `Delay_Resp` で親リンクが確定済みの場合のみ有効です（破線枠 = 未確定リンク）。未確定の Follower が表示する継承GMには `(inherited, unverified)` と付記され、不一致検知はまだ有効でないことを示します。

### 4. Boundary Clock (BC) 識別
v1/v2 変換動作、同一プロトコル内での複数ドメイン間のブリッジ動作、および `via Upstream` 表記による中継状態を自動判別します。

### 5. 高精度 GM 識別
PTPv2 の Announce パケットに加え、PTPv1 (Dante 等) の Sync パケットからも `grandmasterClockUuid` を正しく抽出し、真の GM を特定します。

### 6. BMC (Best Master Clock) 情報表示
PTPv2 の Leader ノードには、Announce パケットから取得した BMC アルゴリズムの判定要素（`Priority1` / `ClockClass` / `Priority2`）を `BMC: P1=... / Class=... / P2=...` として表示します。GM 選出の優先順位確認に利用できます。

### 7. 遅延測定方式 (E2E / P2P) の判別
PTPv2 の各ノードに、現在使用中の遅延測定方式を `Delay:` 行で表示します。
- **E2E (Delay_Req/Resp)**: `Delay_Req` / `Delay_Resp` から判別します。
- **P2P (peer delay)**: `Pdelay_Req` / `Pdelay_Resp` / `Pdelay_Resp_Follow_Up` から判別します。これらは専用のマルチキャストグループ（`224.0.0.107`）を使うため、本ツールは主グループ（`224.0.1.129`）に加えてこちらにも参加します。参加に失敗した場合は `[WARN]` を出力し、E2E の監視は影響を受けずに継続します。
- **E2E + P2P ⚠**: 同一機器から両方式が観測された状態で、異常です。
各方式は、直近 `OfflineTimeoutSeconds` 以内に観測された場合のみ有効とみなします。

Pdelay 通信しか行わないノードは `Unknown` ではなく `P2P node` と表示します。**Pdelay から Role は推定しません**（P2P の Leader も Pdelay_Req を送るため）。この Role 未確定ノードは Pdelay 通信が続く間はオンライン扱いですが、Leader/Follower は従来どおり Role 確定パケット（Sync/Announce/Delay_Req/Delay_Resp）でのみ生存判定します。

**方式混在の警告**: 同一の PTPv2 ドメイン内で E2E 機器と P2P 機器が共存する状態が、保持時間（Conflict と同じ10秒）続くと、該当ノードに黄色の `DELAY MIX` バッジを表示し、警告バナーにドメインを列挙し、エピソードごとに1回 `[DELAY_MECH_MISMATCH]`（各方式の機器数付き）をログに記録します。これは**障害ではなく警告**です。プロファイルや設定の不一致を示すことが多い一方、リンクごとに両端が一致していれば、ドメイン内に異なる方式のリンクが共存すること自体は成立し得ます。PTPv1（IEEE 1588-2002）にはピア遅延方式が存在しないため、評価対象外です。

> [!NOTE]
> P2P の検出には、ピア遅延用マルチキャストグループがこの PC まで届く必要があります（スイッチポートの IGMP 参加など）。現時点では手作りパケットでの検証のみで、実ネットワークでの動作は未確認です。

### 8. Announce のクロック品質と時刻プロパティ
Announce を送信した PTPv2 の Leader ノードには、`BMC:` 行の下にさらに2行を表示します。
- `Clock: Accuracy ... / Variance 0x.... / Source ... / Steps removed N` — `clockAccuracy`、`offsetScaledLogVariance`、`timeSource`、`stepsRemoved`（その Announce が Grandmaster から何ホップ先か）です。
- `Time: UTC offset ... s (valid | NOT valid) / PTP timescale | ARB timescale / time-traceable ✓✗ / freq-traceable ✓✗ [/ leap61] [/ leap59]` — `currentUtcOffset`（符号付き秒）と、Announce の時刻プロパティのフラグです。

補足:
- 生の値は常に表示します。Accuracy と time source の名称は、Wireshark の PTP dissector の表で確認できた値にのみ付け、それ以外は `0xNN (unrecognized)` と表示します。**`clockClass` は数値のみ**（`BMC:` 行に表示）で、意味はプロファイルに依存するためここでは解釈しません。
- `✗` は「未設定」を表すだけで、障害ではありません。たとえば PTP 以外の timescale は、プロファイルによっては正常です。
- 値は、そのノードから**最後に受信した Announce** のものです。受信するまでは `N/A` で、`stepsRemoved` は 63 バイト以上、`timeSource` は 64 バイトの完全な Announce が必要です。行にマウスを重ねると内容の説明が表示されます。
- 表示のみで、これらの値からアラートは出しません。「なぜこの GM が選ばれたか」の順位表示も行いません。Best Master Clock アルゴリズムの比較順がプロファイル（既定・ITU-T G.8275 など）で異なるためです。

### 9. Announce タイムアウトと Announce 間隔の不一致
どちらも**警告**です（黄色のバッジ、警告バナー、エピソードごとに1回のログ、`ANNOUNCE` / `INTERVAL` ログフィルタ）。
- **Announce タイムアウト**（`[ANNOUNCE_TIMEOUT]`、バッジ `NO ANNOUNCE`）: PTPv2 の Leader が、まだ動作中（Sync などを送り続けているためオフライン判定は働かない）にもかかわらず、`AnnounceReceiptTimeout` × 自身の Announce 間隔（Announce から読み取った `2^logMessageInterval` 秒）の間 Announce を送っていない状態です。Grandmaster 交代の前兆になります。上限値は最低 1 秒で（1秒未満の間隔がネットワークの揺らぎで誤検知されないため）、Announce に間隔の指定がない（0x7F）Leader は判定しません。完全に通信が止まった Leader はタイムアウトではなくオフラインとして扱います。Announce が再び届くと解除されます。
- **Announce 間隔の不一致**（`[INTERVAL_MISMATCH]`、バッジ `INTERVAL`）: 同一 PTPv2 ドメインの Leader 同士の Announce 間隔が、保持時間（Conflict と同じ10秒）の間異なる状態です。ログに検出した間隔を列挙します。これは linuxptp の「Announce 間隔はドメイン全体で同じにすべき」という指針に基づくもので、厳密な規則ではない（Boundary Clock が意図的に異なる構成もあり得る）ため、警告に留めます。**Sync 間隔は比較しません**。一致が必要と述べる情報源がないためです。

> [!NOTE]
> Announce 間隔が長い場合（例: 16秒）、上限値は 3 × 16 = 48 秒です。Leader が役割判定用の通信（Sync・Announce など）をすべて止めた場合は、その前にオフライン判定時間でオフライン表示になります。Sync を送り続けている場合は、Announce なしで48秒が経過した時点で報告されます。

### 10. ドメイン見出しのプロファイルヒント
各ドメイン見出しに、そのドメイン**番号**がどの PTP プロファイルに当たるかを示す小さな `hint:` が付くことがあります。これはヒントであり検出結果ではありません。ドメイン番号は利用者が設定するもので、複数のプロファイルが既定値を共有する（特に 0）ため、何かを断定せず、アラートも出しません。
- PTPv2 のドメイン `127`: SMPTE ST 2059-2 の既定ドメイン
- PTPv2 のドメイン `0`: AES67 の既定ドメイン（他のプロファイルでも使われます）
- PTPv2 のドメイン `24`〜`43` / `44`〜`63`: ITU-T G.8275.1 / G.8275.2 の範囲内
- PTPv1: Dante の既定のクロッキングとして表示（サブドメイン名は解釈しません）
- それ以外の番号: ヒントなし。

値はベンダーや標準化団体の文書によるもので、各規格の全文ではありません。IEEE 1588 の既定プロファイルの既定ドメインと、Dante の PTPv1 サブドメイン名は確認できなかったため、意図的に表示していません。

## WebUI の操作
- **Uptime / Offline**: 各デバイスの直近の稼働時間、または切断後の経過時間をリアルタイム表示します。オフラインから復帰するとUptimeがリセットされ、`[REJOIN]` ログが記録されます。
- **検索/フィルタ**: 画面上部の検索ボックスにIP・MAC・ベンダー名を入力すると、該当するノードのみを絞り込み表示します。
- **警告バナー**: トポロジー表示の上部に、現在発生中のConflict/GM不一致アラートが一覧表示されます。クリックすると該当ノードまでスクロールします。
- **ログレベルフィルタ**: イベントログ欄は `ALL` / `ERROR` / `WARN` / `CONFLICT` / `MISMATCH`（GM不一致）/ `DELAY`（E2E/P2P混在）/ `ANNOUNCE`（Announce タイムアウト）/ `INTERVAL`（Announce 間隔不一致）で絞り込み表示できます。
- `🗑 Clear Offline`: オフライン機器を一括削除します。
- `↻ Network Clear`: 全データ（メモリおよびログ）をリセットして監視を再開します。
- **統計パネル**: ネットワーク内で現在「オンライン」の機器のみをカウントして表示します。

## ログとメンテナンス
- **ログファイルの場所**:
    - **最新のログ**: プロジェクト直下の `ptp_monitor.log` に記録されます。
    - **過去のログ**: `logs/` フォルダ内にタイムスタンプ付きで自動保存（ローテーション）されます。
- **自動ローテーション**: プログラム起動時、またはログサイズが 10MB を超えた際に自動で行われます。

## 高度な設定 (config.ini)
- **OfflineRetentionHours**: オフライン機器を保持する時間（デフォルト: 24時間）。
- **ExpectedDelayInterval**: Follower の `Delay_Req` の期待間隔（秒）。
- **DelayAlertThresholdRate**: アラートを出す倍率（デフォルト 1.5）。実際の間隔が 「期待値 × 倍率」 を超えると赤字警告。
- **OfflineTimeoutSeconds**: オフライン判定までの無通信時間（デフォルト 10.0秒）。
- **AnnounceReceiptTimeout**: 動作中の Leader が Announce を送っていないと報告するまでの、Announce 間隔の欠落回数（デフォルト 3、範囲 2〜255。linuxptp の `announceReceiptTimeout` と同じ意味・既定値）。範囲外の値は無視されます。
- **OUI Vendor Mapping**: MACアドレスの先頭（OUI）に基づくベンダー名の定義。
- **Specific Device Mapping**: 特定のMACアドレスに対して、ホスト名や機器名を個別に割り当て可能。

> [!NOTE]
> 数値設定に不正な値（数値として解釈できない文字列など）を記述した場合、その項目は無視され、既定値が維持されます（起動時のコンソールに `[WARN]` として通知されます）。数値の小数点は地域設定に関わらずピリオド（`.`）で記述してください。
