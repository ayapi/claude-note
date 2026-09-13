"""ClaudeNote が実際に消費したトークンと、API 従量課金だった場合の費用を集計する。

Claude Code のセッション JSONL (~/.claude/projects/<エンコードされた作業ディレクトリ>/*.jsonl)
には 1 往復ごとの usage が残っている。ここから ClaudeNote が投げた 1 送信ぶんを切り出し、
送信あたり・日あたりの費用を出す。サブスクリプションと API のどちらが得かを判断したり、
プロンプトや設定を変えた効果を測るのに使う。

使い方:
    python tools/usage-report.py                     # 対象になりそうなプロジェクトを一覧
    python tools/usage-report.py C--Users-me-repo    # そのプロジェクトを集計
    python tools/usage-report.py <パス>              # ディレクトリを直接指定してもよい

注意: 同じリポジトリで対話 CLI も使っていると、その往復が同じファイルに混ざる。
ClaudeNote のプロンプトに含まれる目印で送信の始まりを見つけ、次のプロンプトまでを
その送信ぶんとして数えることで切り分けている。
"""
import glob
import io
import json
import os
import sys
from collections import defaultdict

# $/1M トークン。キャッシュ読みは入力単価の 0.1 倍、書き込みは 1.25 倍 (5 分 TTL)。
PRICE = {
    "Fable 5": {"in": 10.0, "out": 50.0},
    "Opus 5": {"in": 5.0, "out": 25.0},
    "Sonnet 5": {"in": 2.0, "out": 10.0},
    "Haiku 4.5": {"in": 1.0, "out": 5.0},
}
CACHE_WRITE_MULT = 1.25
CACHE_READ_MULT = 0.10

# ClaudeNote が組み立てたプロンプトの目印 (appsettings.json の各テンプレートに由来)
NOTE_MARKERS = (
    "そのまま OneNote に挿入",
    "手書きノートについて口頭で",
    "手書きノートの続き",
    "OneNote の手書きノート",
    "ノート上で選択されていたテキスト",
)

PROJECTS = os.path.expanduser(os.path.join("~", ".claude", "projects"))


def cost(d, price):
    return (
        d["in"] * price["in"]
        + d["cw"] * price["in"] * CACHE_WRITE_MULT
        + d["cr"] * price["in"] * CACHE_READ_MULT
        + d["out"] * price["out"]
    ) / 1_000_000


def user_text(msg):
    """ユーザーメッセージの本文。tool_result だけなら None (送信ではなく会話の続き)。"""
    content = msg.get("content")
    if isinstance(content, str):
        return content
    if not isinstance(content, list):
        return None
    texts, has_tool_result = [], False
    for block in content:
        if not isinstance(block, dict):
            continue
        if block.get("type") == "tool_result":
            has_tool_result = True
        elif block.get("type") == "text":
            texts.append(block.get("text", ""))
    if has_tool_result and not texts:
        return None
    return "\n".join(texts)


def collect(root):
    """(ClaudeNote の送信ごとの usage, それ以外の合計) を返す。"""
    sends, other = [], defaultdict(int)
    for path in sorted(glob.glob(os.path.join(root, "*.jsonl"))):
        current = None
        for line in io.open(path, encoding="utf-8", errors="replace"):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            msg = rec.get("message") or {}

            if rec.get("type") == "user":
                text = user_text(msg)
                if text is not None:  # 新しいプロンプト = 区切り
                    if any(m in text for m in NOTE_MARKERS):
                        current = {
                            "day": (rec.get("timestamp") or "")[:10],
                            "turns": 0, "in": 0, "out": 0, "cw": 0, "cr": 0,
                        }
                        sends.append(current)
                    else:
                        current = None
                continue

            usage = msg.get("usage")
            if not usage:
                continue
            target = current if current is not None else other
            target["turns"] += 1
            target["in"] += usage.get("input_tokens", 0) or 0
            target["out"] += usage.get("output_tokens", 0) or 0
            target["cw"] += usage.get("cache_creation_input_tokens", 0) or 0
            target["cr"] += usage.get("cache_read_input_tokens", 0) or 0
    return [s for s in sends if s["turns"] > 0], other


def list_projects():
    print("集計できそうなプロジェクト:\n")
    for d in sorted(glob.glob(os.path.join(PROJECTS, "*"))):
        if not os.path.isdir(d):
            continue
        files = glob.glob(os.path.join(d, "*.jsonl"))
        if not files:
            continue
        size = sum(os.path.getsize(f) for f in files) / 1048576
        print(f"  {os.path.basename(d):<60} {len(files):>3} ファイル {size:>7.1f} MB")
    print("\n上のどれかを引数に渡してください。")


def main():
    if len(sys.argv) < 2:
        list_projects()
        return 0

    arg = sys.argv[1]
    root = arg if os.path.isdir(arg) else os.path.join(PROJECTS, arg)
    if not os.path.isdir(root):
        print(f"見つかりません: {root}")
        return 1

    sends, other = collect(root)
    if not sends:
        print("ClaudeNote 由来の送信が見つかりませんでした。")
        return 1

    by_day = defaultdict(list)
    for s in sends:
        by_day[s["day"]].append(s)

    print(f"ClaudeNote の 1 送信を単位にした実績 ({os.path.basename(root)})\n")
    header = (f"{'日付':<12}{'送信':>5}{'API往復':>8}{'往復/送信':>10}"
              f"{'出力tok':>10}{'総入力tok':>13}{'文脈/往復':>11}")
    for name in PRICE:
        header += f"{name.split()[0]:>9}"
    print(header)
    print("-" * len(header))

    total, total_sends = defaultdict(int), 0
    for day in sorted(by_day):
        group = by_day[day]
        d = defaultdict(int)
        for s in group:
            for k in ("turns", "in", "out", "cw", "cr"):
                d[k] += s[k]
        total_in = d["in"] + d["cw"] + d["cr"]
        row = (f"{day:<12}{len(group):>5}{d['turns']:>8}{d['turns']/len(group):>10.1f}"
               f"{d['out']:>10,}{total_in:>13,}{total_in/d['turns']:>11,.0f}")
        for price in PRICE.values():
            row += f"{cost(d, price):>9.2f}"
        print(row)
        for k in ("turns", "in", "out", "cw", "cr"):
            total[k] += d[k]
        total_sends += len(group)

    total_in = total["in"] + total["cw"] + total["cr"]
    row = (f"{'合計':<12}{total_sends:>5}{total['turns']:>8}"
           f"{total['turns']/total_sends:>10.1f}{total['out']:>10,}"
           f"{total_in:>13,}{total_in/total['turns']:>11,.0f}")
    for price in PRICE.values():
        row += f"{cost(total, price):>9.2f}"
    print("-" * len(header))
    print(row)

    print(f"\n1 送信あたり ({total_sends} 送信の平均):")
    for name, price in PRICE.items():
        per = cost(total, price) / total_sends
        print(f"  {name:<10} ${per:.3f} / 送信   20問の日 ${per*20:>7.2f}   "
              f"30日×20問 ${per*20*30:>9.2f}")

    per_send = sorted(cost(s, PRICE["Fable 5"]) for s in sends)
    n = len(per_send)
    print(f"\n送信ごとのばらつき (Fable 5): 最小 ${per_send[0]:.2f} / "
          f"中央 ${per_send[n//2]:.2f} / 平均 ${sum(per_send)/n:.2f} / 最大 ${per_send[-1]:.2f}")

    if other["turns"]:
        print(f"\n同じリポジトリでの対話 CLI 作業: {other['turns']} 往復 / "
              f"Fable5 ${cost(other, PRICE['Fable 5']):.2f} (ClaudeNote とは別)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
