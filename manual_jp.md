# PTPMonitor 使用説明書 (v1.6.8)

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
同一ドメイン内に複数の Leader が存在する場合、その状態の深刻度を時間軸で判定します。
- **BMCA (Negotiating) [黄色]**: 複数のリーダーを検知してから **10秒以内**。マスター切り替えに伴う正常な交渉プロセスとして表示されます。
- **CONFLICT (Persistent) [赤色]**: 競合が **10秒以上** 継続している状態。ネットワーク上の設定ミスや二重マスター障害としてログ (`[CONFLICT_ALERT]`) に記録されます。

### 3. GM 不一致アラート (Diagnostic)
Follower が親（Leader/BC）と異なる Grandmaster ID を受信している場合に、WebUI で赤色警告 (`⚠️ GM Mismatch!`) を出し、トポロジーの乱れを即座に通知します。

### 4. Boundary Clock (BC) 識別
v1/v2 変換動作、同一プロトコル内での複数ドメイン間のブリッジ動作、および `via Upstream` 表記による中継状態を自動判別します。

### 5. 高精度 GM 識別
PTPv2 の Announce パケットに加え、PTPv1 (Dante 等) の Sync パケットからも `grandmasterClockId` を正しく抽出し、真の GM を特定します。

## WebUI の操作
- **Uptime / Offline**: 各デバイスの直近の稼働時間、または切断後の経過時間をリアルタイム表示します。
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
- **OUI Vendor Mapping**: MACアドレスの先頭（OUI）に基づくベンダー名の定義。
- **Specific Device Mapping**: 特定のMACアドレスに対して、ホスト名や機器名を個別に割り当て可能。
