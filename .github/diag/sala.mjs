// Testa o servidor gratuito do PeerJS (0.peerjs.com) do mesmo jeito que o app usa.
import WebSocket from "ws";
const base = "0.peerjs.com";
const t0 = Date.now();
async function get(path){ const r = await fetch(`https://${base}${path}`); return [r.status, (await r.text()).slice(0, 120)]; }
console.log("GET /", await get("/").catch(e => e.message));
console.log("GET /peerjs/id", await get("/peerjs/id?ts=" + Date.now()).catch(e => e.message));
function open(id){
  return new Promise(res => {
    const ws = new WebSocket(`wss://${base}/peerjs?key=peerjs&id=${id}&token=${Math.random().toString(36).slice(2)}&version=1.5.4`);
    const out = { id, msgs: [] , ws };
    const to = setTimeout(() => { out.timeout = true; res(out); }, 15000);
    ws.on("message", m => { const d = JSON.parse(m); out.msgs.push(d.type); if (d.type === "OPEN" || d.type === "ERROR" || d.type === "ID-TAKEN") { clearTimeout(to); res(out); } });
    ws.on("error", e => { out.err = e.message; clearTimeout(to); res(out); });
    ws.on("close", c => { out.close = c; });
  });
}
const room = "sessao-diag-" + Math.random().toString(36).slice(2, 8);
const a = await open(room);
console.log("sala (transmissor):", a.msgs, a.err || "", a.timeout ? "SEM RESPOSTA" : "", (Date.now() - t0) + "ms");
const b = await open("viewer-" + Math.random().toString(36).slice(2, 10));
console.log("amigo:", b.msgs, b.err || "", b.timeout ? "SEM RESPOSTA" : "");
// o amigo manda um "oi" para a sala pelo servidor; a sala precisa receber
const got = new Promise(res => { a.ws.on("message", m => { const d = JSON.parse(m); if (d.type === "OFFER") res("sala recebeu o pedido do amigo"); }); setTimeout(() => res("sala NÃO recebeu o pedido"), 10000); });
b.ws.send(JSON.stringify({ type: "OFFER", dst: room, payload: { type: "data", connectionId: "dc_test", sdp: { type: "offer", sdp: "v=0" } } }));
console.log(await got);
// mesmo código registrado de novo enquanto o antigo ainda está ligado (o que acontece ao religar)
const c = await open(room);
console.log("mesmo código de novo:", c.msgs);
process.exit(0);
