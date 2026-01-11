# LocalTempClear / ローカル一時フォルダ削除ツール ✅

**A simple command-line tool to locate and remove old temporary files (default: dry-run).**

**日本語 / English (日英併記)**

---

## 🛠 概要 / Overview
- 一時フォルダ内の古いファイルや空ディレクトリを検出して削除します（デフォルトは dry-run）。 — Finds and removes old files and empty directories from the specified temporary path (default: dry-run).
- 指定した日数より古いファイルを対象にします（デフォルト: 7日）。 — Removes files older than the specified number of days (default: 7).
- トップレベルのすべてを削除するモード（`--delete-all`）。 — Top-level delete mode (`--delete-all`) removes all top-level entries under the temp path.
- 自動で最適な並列度を判定するか、手動で指定できます（`--parallel`）。 — By default, the tool attempts to detect an appropriate degree of parallelism; you may override this with `--parallel`.
- I/O スロットリング（`--throttle`）、ログ出力（`--log`）、冗長モード（`--verbose`）に対応します。 — Supports I/O throttling (`--throttle`), file logging (`--log`), and verbose output (`--verbose`).

---

## 🚀 ビルド / Build
```bash
dotnet build
```

Publish / 発行（例: Windows x64 の自己完結型）:
```bash
dotnet publish -c Release -r win-x64 --self-contained -o ./publish
```

---

## ▶️ 使い方 / Usage
```
Usage: LocalTempClear [options]
  -r, --run           Execute deletions (otherwise dry-run) — 削除を実行（指定しない場合は dry-run）
  -d, --days <n>      Delete files older than n days (default: 7) — n 日より古いファイルを削除（デフォルト: 7）
  -p, --path <path>   Use specific temp path (default: %TEMP%) — 一時パスを指定（デフォルト: %TEMP%）
  -P, --parallel <n>  Run deletions in parallel with up to n concurrent tasks (default: 1 = no parallelism) — 最大 n 並列で削除（デフォルト 1 = 並列化なし）
  -a, --delete-all    Delete everything under the temp path (top-level) — 一時パス直下のすべてを削除（トップレベル）
  -t, --throttle <bytes/s>  Throttle I/O to approximately bytes per second — I/O をおおよそ bytes/s に制限
  -l, --log <path>    Write detailed logs to the specified file — 詳細ログを指定ファイルに出力
  -v, --verbose       Enable verbose logging — 冗長ログを有効化
  --no-auto-detect    Disable automatic parallelism detection — 自動並列判定を無効化
  -h, --help          Show this help — ヘルプを表示
```

実行例 / Examples:
- `LocalTempClear` — dry-run（削除は実行されません）
- `LocalTempClear -r` — execute deletions（削除を実行）
- `LocalTempClear -r -d 30 -p C:\Temp` — delete files older than 30 days in specific path（指定パスで 30 日より古いファイルを削除）
- `LocalTempClear -r -a` — delete top-level entries（トップレベルのエントリを削除）
- `LocalTempClear -r -t 1048576` — throttle to 1 MB/s（1 MB/s にスロットル）

---

## ⚠️ 注意 / Notes
- ファイルがロックされているか権限不足のため削除できない場合があります。 — Some files may be locked or require elevated permissions and cannot be deleted.
- 管理者（昇格した）権限が必要な場合があります。 — Administrator (elevated) privileges may be required to delete certain files.

---

## 🤝 貢献 / Contributing
**Pull requests are welcome; creating an issue beforehand is optional.**
- 小さな修正やドキュメント更新は直接 PR で問題ありません。 — Small fixes and documentation updates can be submitted via pull request.
- 大きな設計変更を提案する場合は、PR に簡単な説明を付けてください。 — For larger changes, please include a brief description in your pull request.

---

## 📄 ライセンス / License
このリポジトリは **MIT ライセンス** の下で提供されます。詳細は `LICENSE` ファイルを参照してください。 — This repository is licensed under the MIT License. See the `LICENSE` file for details.

---

## 📌 その他 / Notes
- メッセージはリソースファイル（`Resources.resx`, `Resources.ja.resx`）で管理されています。 — Messages and user-facing text are managed via resource files (`Resources.resx`, `Resources.ja.resx`).

---

(必要なら README の文言や英訳、LICENSE の著作権表記をカスタマイズします。)
