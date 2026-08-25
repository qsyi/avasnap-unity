# AvaSnap連携 (jp.qsyi.avasnap)

デスクトップアプリ[AvaSnap](https://github.com/qsyi/AvaSnap)との連携用Unityエディタ拡張です。

## カメラ構図補助線

`Tools > qsyi > カメラ構図補助線 (AvaSnap連携)` から開けます。

AvaSnapの位置合わせモードで「取得」ボタンが押されるたびに、Unityエディタ上の対象カメラ(未指定なら`Camera.main`)の実際のFOV・ピッチ・ロールを一度だけファイルに書き出します(エディタモード/プレイモード問わず)。AvaSnap側はこれを読み取り、遠近ガイド線として表示します(要AvaSnap側の対応)。

リクエストが来るまでUnity側は何も処理しません(`FileSystemWatcher`でリクエストファイルの出現を待つだけで、常時ポーリングはしていません)。Unityでカメラを動かしても自動では追従せず、押されるたびのスナップショット取得です。出力先は `%AppData%\AvaSnap\unity_camera_guide.json` です。
