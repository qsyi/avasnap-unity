# AvaSnap連携 (jp.qsyi.avasnap)

デスクトップアプリ[AvaSnap](https://github.com/qsyi/AvaSnap)との連携用Unityエディタ拡張です。

## カメラ構図補助線

`Tools > qsyi > カメラ構図補助線 (AvaSnap連携)` から開けます。

Unityエディタ上の対象カメラ(未指定なら`Camera.main`)の実際のFOV・ピッチ・ロールを、エディタモード/プレイモード問わず約6.7Hzでファイルに書き出します。AvaSnapの位置合わせモードがこれを読み取り、遠近ガイド線として表示します(要AvaSnap側の対応)。

このUnityエディタウィンドウがOSフォーカスを持っている間だけ書き出します。出力先は `%AppData%\AvaSnap\unity_camera_guide.json` です。
