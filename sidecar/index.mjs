// ClaudeNote サイドカー: Claude Agent SDK を常駐プロセスとして提供する。
// プロトコル: stdin/stdout の JSON Lines。
//   要求:   {"id":"1","prompt":"...","resume":"<sessionId>|null","cwd":"...","addDirs":["..."],"model":null}
//   中断:   {"id":"1","cancel":true}
//   応答:   {"id":"1","ok":true,"text":"...","sessionId":"..."}
//         | {"id":"1","ok":false,"error":"...","resumeFailed":true?,"canceled":true?}
//   進行:   {"id":"1","event":"progress","kind":"...","detail":"..."}
// ログはすべて stderr に出す (stdout はプロトコル専用)。
//
// セッションは張りっぱなしにする。SDK のストリーミング入力モードで query() を 1 本
// 開いたままにしておくと、2 回目以降の往復で claude プロセスの起動と init を丸ごと
// 省ける (実測 1.3〜1.7 秒/往復)。要求の形 (cwd/addDirs/allowedTools/model) が変わるか、
// 継続したいセッションが手元の常駐セッションと違うときだけ張り直す。
import { createInterface } from "node:readline";
import { query } from "@anthropic-ai/claude-agent-sdk";

const log = (m) => process.stderr.write(`[sidecar] ${m}\n`);
const respond = (obj) => process.stdout.write(JSON.stringify(obj) + "\n");

// 使われないまま放置された常駐セッションを畳むまでの時間
const IDLE_MS = 30 * 60 * 1000;

/** 外から push できる非同期イテレータ。SDK のストリーミング入力に食わせる。 */
function createPushable() {
  const queue = [];
  let waiting = null;
  let closed = false;
  return {
    push(value) {
      if (waiting) {
        const resolve = waiting;
        waiting = null;
        resolve({ value, done: false });
      } else {
        queue.push(value);
      }
    },
    close() {
      closed = true;
      if (waiting) {
        const resolve = waiting;
        waiting = null;
        resolve({ done: true });
      }
    },
    async *[Symbol.asyncIterator]() {
      while (true) {
        if (queue.length > 0) {
          yield queue.shift();
          continue;
        }
        if (closed) return;
        const next = await new Promise((resolve) => (waiting = resolve));
        if (next.done) return;
        yield next.value;
      }
    },
  };
}

const userMessage = (content) => ({
  type: "user",
  message: { role: "user", content },
  parent_tool_use_id: null,
});

/** 常駐セッションを張り直すべきかの判定に使う、要求の「形」。 */
const shapeKey = (req) =>
  JSON.stringify({
    cwd: req.cwd || null,
    addDirs: Array.isArray(req.addDirs) ? req.addDirs : [],
    allowedTools:
      Array.isArray(req.allowedTools) && req.allowedTools.length > 0
        ? req.allowedTools
        : ["Read", "Glob", "Grep"],
    model: req.model || null,
  });

/** 現在生きている常駐セッション。C# 側が要求を直列化するので同時にひとつでよい。 */
let live = null;

function startSession(req, key) {
  const shape = JSON.parse(key);
  const input = createPushable();
  const session = {
    key,
    input,
    sessionId: req.resume || null,
    turn: null,
    idleTimer: null,
    closing: false,
  };
  session.q = query({
    prompt: input,
    options: {
      cwd: shape.cwd || undefined,
      resume: req.resume || undefined,
      additionalDirectories: shape.addDirs,
      // 自動許可するツールは C# 側 (appsettings.json の allowedTools) が決める。
      // 未指定時は読み取り専用にフォールバック
      allowedTools: shape.allowedTools,
      model: shape.model || undefined,
      includePartialMessages: true, // 進行イベントを流して C# 側の無応答検知に使う
    },
  });
  void pump(session);
  log(`常駐セッションを開始 (resume=${req.resume ?? "なし"}, cwd=${shape.cwd ?? "既定"})`);
  return session;
}

function closeSession(session, reason) {
  if (!session || session.closing) return;
  session.closing = true;
  if (session.idleTimer) clearTimeout(session.idleTimer);
  if (live === session) live = null;
  log(`常駐セッションを終了: ${reason}`);
  try {
    session.input.close();
  } catch {
    // 既に閉じている
  }
}

/** SDK からのメッセージを読み続け、result が来たらそのターンを完了させる。 */
async function pump(session) {
  let lastProgress = 0;
  // 進行イベント。stream はトークン単位で来るので 5 秒に 1 回に間引く
  const progress = (kind, detail) => {
    const turn = session.turn;
    if (!turn) return;
    const now = Date.now();
    if (kind === "stream" && now - lastProgress < 5000) return;
    lastProgress = now;
    respond({ id: turn.id, event: "progress", kind, detail: detail || null });
  };

  try {
    for await (const msg of session.q) {
      if (msg.type === "result") {
        session.sessionId = msg.session_id ?? session.sessionId;
        const turn = session.turn;
        session.turn = null;
        turn?.resolve(msg);
        continue;
      }
      if (msg.type === "system") {
        session.sessionId = msg.session_id ?? session.sessionId;
        progress("system");
        continue;
      }
      if (msg.type === "stream_event") {
        progress("stream");
      } else if (msg.type === "assistant") {
        const tools = (msg.message?.content ?? [])
          .filter((b) => b.type === "tool_use")
          .map((b) => `${b.name}${b.input?.file_path ? " " + b.input.file_path : ""}`);
        progress("assistant", tools.join(", "));
      } else {
        progress(msg.type);
      }
    }
    session.turn?.reject(new Error("SDK から result メッセージが返らないままセッションが終了しました"));
  } catch (e) {
    session.turn?.reject(e instanceof Error ? e : new Error(String(e)));
  } finally {
    session.turn = null;
    closeSession(session, "SDK のストリームが終了しました");
  }
}

async function handle(req) {
  const { id, prompt, resume } = req;
  const key = shapeKey(req);

  // 常駐セッションを使い回せるのは、要求の形が同じで、かつ継続したいセッションが
  // 手元のものと一致するときだけ。resume が null = 新しい会話の指示なので張り直す。
  let session = live;
  if (session) {
    if (session.key !== key) closeSession(session, "設定 (cwd/ツール/モデル) が変わりました");
    else if (!resume) closeSession(session, "新しい会話を開始します");
    else if (resume !== session.sessionId) closeSession(session, `別のセッションへの継続要求 (${resume})`);
    session = live;
  }

  const reused = session != null;
  if (!session) {
    session = startSession(req, key);
    live = session;
  } else if (session.idleTimer) {
    clearTimeout(session.idleTimer);
    session.idleTimer = null;
  }
  log(`要求 #${id}: ${reused ? "常駐セッションを再利用" : "セッションを新規作成"}`);

  const turn = { id, canceled: false, resolve: null, reject: null };
  const result = new Promise((resolve, reject) => {
    turn.resolve = resolve;
    turn.reject = reject;
  });
  session.turn = turn;

  try {
    session.input.push(userMessage(prompt));
    const msg = await result;

    if (turn.canceled) {
      log(`canceled: #${id}`);
      respond({ id, ok: false, error: "キャンセルされました", canceled: true });
      return;
    }
    const sessionId = session.sessionId;
    if (msg.subtype === "success" && !msg.is_error) {
      respond({ id, ok: true, text: msg.result, sessionId });
      return;
    }
    const text = msg.subtype === "success" ? msg.result : `result: ${msg.subtype}`;
    // resume の失敗として C# 側に引き継ぎを促してよいのは、この要求で実際に
    // resume 付きのセッションを張ろうとしたときだけ。使い回した場合は別の失敗。
    respond({ id, ok: false, error: text, sessionId, resumeFailed: !reused && Boolean(resume) });
  } catch (e) {
    const message = String(e?.message ?? e);
    if (turn.canceled) {
      log(`canceled: #${id}`);
      respond({ id, ok: false, error: "キャンセルされました", canceled: true });
    } else {
      log(`error: ${message}`);
      respond({ id, ok: false, error: message, resumeFailed: !reused && Boolean(resume) });
    }
  } finally {
    if (session.turn === turn) session.turn = null;
    if (!session.closing) {
      session.idleTimer = setTimeout(
        () => closeSession(session, `${IDLE_MS / 60000} 分間使われませんでした`),
        IDLE_MS,
      );
      session.idleTimer.unref?.();
    }
  }
}

function cancel(id) {
  const turn = live?.turn;
  if (!turn || turn.id !== id) {
    log(`cancel: #${id} は実行中ではありません`);
    return;
  }
  const session = live;
  log(`cancel: #${id} を中断します`);
  turn.canceled = true;
  // interrupt() はターンだけを止めてセッションは残すので、次の要求で使い回せる
  session.q.interrupt().catch((e) => {
    log(`interrupt に失敗、セッションを畳みます: ${e?.message ?? e}`);
    closeSession(session, "interrupt に失敗しました");
  });
}

let pending = 0;
let stdinClosed = false;
const maybeExit = () => {
  if (stdinClosed && pending === 0) process.exit(0);
};

const rl = createInterface({ input: process.stdin, terminal: false });
rl.on("line", (line) => {
  line = line.trim();
  if (!line) return;
  let req;
  try {
    req = JSON.parse(line);
  } catch (e) {
    respond({ id: null, ok: false, error: `不正な要求 JSON: ${e.message}` });
    return;
  }
  if (req.cancel) {
    cancel(req.id);
    return;
  }
  pending++;
  void handle(req).finally(() => {
    pending--;
    maybeExit();
  });
});
// stdin が閉じても処理中の要求は完了させてから終了する
rl.on("close", () => {
  stdinClosed = true;
  closeSession(live, "stdin が閉じました");
  maybeExit();
});

log(`ready (node ${process.version})`);
