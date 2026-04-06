# CHANGELOG

## [v1.6.7] - 2026-04-06
### Added
- **監視閾値のカスタマイズ機能 (config.ini)**:
    - `ExpectedDelayInterval`: Delay_Req の基準間隔（秒）を設定可能に。
    - `DelayAlertThresholdRate`: アラートを出す倍率（閾値）を設定可能に。
    - `OfflineTimeoutSeconds`: デバイスをオフラインと判定するまでの秒数を設定可能に。
- **動的アラート表示 (Web UI)**:
    - 上記設定に基づき、基準を超えた際に `Delay Intv` を赤字で強調表示するロジックを実装。

### Fixed
- Web UI において `Delay Interval` の計測値が表示されない不具合を解消。

## [v1.6.6] - 2026-04-06
### Added
- **Delay Request 間隔計測**:
    - Follower が送信する `Delay_Req` パケットの受信間隔（Delay Interval）をリアルタイムで計測するロジックを実装。
    - Web UI の `Uptime` 右側に計測値を表示。

## [v1.6.4] - 2026-04-06
### Changed
- **PTPv1 UI 最適化**:
    - PTPv1 (Dante等) において、通常運用では不要なインターフェース値（Sync/Announce Log 等）を非表示にし、視認性を向上。

## [v1.6.1] - 2026-04-04
### Fixed
- **Uptime 表示の安定化**:
    - 起動直後の稼働時間カウントダウンおよび表示ロジックを修正。
- **マニュアル更新**:
    - v1.6.x 系の新機能に対応した使用説明書および config.ini の記述を整備。
