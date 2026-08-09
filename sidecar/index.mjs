// ClaudeNote サイドカー: Claude Agent SDK を常駐プロセスとして提供する。
// プロトコル: stdin/stdout の JSON Lines。
//   要求: {"id":"1","prompt":"...","resume":"<sessionId>|null","cwd":"...","addDirs":["..."],"model":null}
//   応答: {"id":"1","ok":true,"text":"...","sessionId":"..."}
//       | {"id":"1","ok":false,"error":"...","resumeFailed":true?}
// ログはすべて stderr に出す (stdout はプロトコル専用)。
import { createInterface } from "node:readline";
import { query } from "@anthropic-ai/claude-agent-sdk";

const log = (m) => process.stderr.write(`[sidecar] ${m}\n`);
const respond = (obj) => process.stdout.write(JSON.stringify(obj) + "\n");

async function handle(req) {
  const { id, prompt, resume, cwd, addDirs, allowedTools, model } = req;
  try {
    const options = {
      cwd: cwd || undefined,
      resume: resume || undefined,
      additionalDirectories: Array.isArray(addDirs) ? addDirs : [],
      // 自動許可するツールは C# 側 (appsettings.json の allowedTools) が決める。
      // 未指定時は読み取り専用にフォールバック
      allowedTools:
        Array.isArray(allowedTools) && allowedTools.length > 0
          ? allowedTools
          : ["Read", "Glob", "Grep"],
      model: model || undefined,
      includePartialMessages: true, // 進行イベントを流して C# 側の無応答検知に使う
    };
    let text = null;
    let sessionId = null;
    let isError = false;
    let lastProgress = 0;
    // 進行イベント。stream はトークン単位で来るので 5 秒に 1 回に間引く
    const progress = (kind, detail) => {
      const now = Date.now();
      if (kind === "stream" && now - lastProgress < 5000) return;
      lastProgress = now;
      respond({ id, event: "progress", kind, detail: detail || null });
    };
    for await (const msg of query({ prompt, options })) {
      if (msg.type === "result") {
        sessionId = msg.session_id ?? sessionId;
        if (msg.subtype === "success" && !msg.is_error) {
          text = msg.result;
        } else {
          isError = true;
          text = msg.subtype === "success" ? msg.result : `result: ${msg.subtype}`;
        }
      } else if (msg.type === "stream_event") {
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
    if (text == null) throw new Error("SDK から result メッセージが返りませんでした");
    if (isError) {
      respond({ id, ok: false, error: text, sessionId, resumeFailed: Boolean(resume) });
    } else {
      respond({ id, ok: true, text, sessionId });
    }
  } catch (e) {
    const message = String(e?.message ?? e);
    log(`error: ${message}`);
    respond({ id, ok: false, error: message, resumeFailed: Boolean(resume) });
  }
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
  pending++;
  void handle(req).finally(() => {
    pending--;
    maybeExit();
  });
});
// stdin が閉じても処理中の要求は完了させてから終了する
rl.on("close", () => {
  stdinClosed = true;
  maybeExit();
});

log(`ready (node ${process.version})`);
