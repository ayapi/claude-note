// index.mjs をプロトコル越しに叩く結合テスト。常駐セッションの寿命を検証する:
// 新規 → 再利用 → キャンセル → キャンセル後の再利用 → 設定変更で張り直し → 新しい会話。
//
// 実際に Claude を呼ぶのでトークンを消費する (小さいプロンプト 6 往復ぶん)。
// 実行: node sidecar/test-session.mjs
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const proc = spawn(process.execPath, [join(here, "index.mjs")], { stdio: ["pipe", "pipe", "pipe"] });
const stderr = [];
createInterface({ input: proc.stderr }).on("line", (l) => stderr.push(l));

const waiters = new Map();
createInterface({ input: proc.stdout }).on("line", (line) => {
  const m = JSON.parse(line);
  if (m.event === "progress") return;
  waiters.get(m.id)?.(m);
  waiters.delete(m.id);
});

let n = 0;
function send(req) {
  const id = String(++n);
  const p = new Promise((r) => waiters.set(id, r));
  proc.stdin.write(JSON.stringify({ id, ...req }) + "\n");
  return { id, done: p };
}
const cancel = (id) => proc.stdin.write(JSON.stringify({ id, cancel: true }) + "\n");
const took = (t0) => `${Date.now() - t0}ms`;

const base = { cwd: here, addDirs: [], allowedTools: ["Bash"], model: null };
let ok = true;
const check = (label, cond, extra = "") => {
  console.log(`  ${cond ? "PASS" : "FAIL"}  ${label}${extra ? "  " + extra : ""}`);
  if (!cond) ok = false;
};

console.log("1) 新規セッション");
let t0 = Date.now();
let r = await send({ ...base, prompt: "合言葉は「あおぞら」。覚えた、とだけ答えて", resume: null }).done;
const sid = r.sessionId;
check("成功して sessionId が返る", r.ok === true && !!sid, `${took(t0)} session=${sid?.slice(0, 8)}`);

console.log("2) 同じセッションを継続 (常駐セッションの再利用を期待)");
t0 = Date.now();
const before = stderr.length;
r = await send({ ...base, prompt: "1+1は？数字だけ", resume: sid }).done;
const reusedLog = stderr.slice(before).some((l) => l.includes("常駐セッションを再利用"));
check("成功する", r.ok === true, `${took(t0)} -> ${String(r.text).trim().slice(0, 12)}`);
check("常駐セッションを再利用した", reusedLog);
check("sessionId が変わらない", r.sessionId === sid);

console.log("3) 実行中のキャンセル");
t0 = Date.now();
const job = send({ ...base, prompt: "Bash で `sleep 60` を実行してから done と言って", resume: sid });
await new Promise((r) => setTimeout(r, 4000));
cancel(job.id);
r = await job.done;
check("canceled として返る", r.ok === false && r.canceled === true, took(t0));
check("resumeFailed を立てない", !r.resumeFailed);

console.log("4) キャンセル後も同じセッションで続けられる (文脈が残っている)");
t0 = Date.now();
const before4 = stderr.length;
r = await send({ ...base, prompt: "さっきの合言葉は？その語だけ答えて", resume: sid }).done;
check("成功する", r.ok === true, `${took(t0)} -> ${String(r.text).trim().slice(0, 12)}`);
check("文脈が残っている (あおぞら)", String(r.text).includes("あおぞら"));
check("キャンセル後もセッションを使い回せた", stderr.slice(before4).some((l) => l.includes("常駐セッションを再利用")));

console.log("5) 設定 (cwd) が変わったら張り直す");
const before5 = stderr.length;
r = await send({ ...base, cwd: join(here, ".."), prompt: "ok とだけ答えて", resume: sid }).done;
check("張り直しのログが出る", stderr.slice(before5).some((l) => l.includes("設定 (cwd/ツール/モデル) が変わりました")));
check("新規作成になる", stderr.slice(before5).some((l) => l.includes("セッションを新規作成")));

console.log("6) resume=null なら新しい会話として張り直す");
const before6 = stderr.length;
r = await send({ ...base, cwd: join(here, ".."), prompt: "合言葉を覚えてる？覚えてないなら「知らない」とだけ", resume: null }).done;
check("新しい会話ですと出る", stderr.slice(before6).some((l) => l.includes("新しい会話を開始します")));
check("前の文脈を引き継いでいない", !String(r.text).includes("あおぞら"), `-> ${String(r.text).trim().slice(0, 16)}`);

proc.stdin.end();
await new Promise((r) => proc.on("exit", r));
console.log(ok ? "\n結果: すべて PASS" : "\n結果: FAIL あり");
process.exit(ok ? 0 : 1);
