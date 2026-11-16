// Testa o servidor gratuito do PeerJS (0.peerjs.com) do mesmo jeito que o app usa, várias vezes.
import WebSocket from "ws";
const base = "0.peerjs.com";
function open(id){
  return new Promise(res => {
    const ws = new WebSocket(`wss://${base}/peerjs?key=peerjs&id=${id}&token=${Math.random().toString(36).slice(2)}&version=1.5.4`);
    const out = { id, msgs: [], ws };
    const to = setTimeout(() => { out.timeout = true; res(out); }, 15000);
    ws.on("message", m => { const d = JSON.parse(m); out.msgs.push(d.type); if (["OPEN","ERROR","ID-TAKEN"].includes(d.type)) { clearTimeout(to); res(out); } });
    ws.on("error", e => { out.err = e.message; clearTimeout(to); res(out); });
  });
}
let ok = 0, n = 0;
for (let i = 0; i < 4; i++) {
  const room = "sessao-diag-" + Math.random().toString(36).slice(2, 8);
  const a = await open(room);
  for (let j = 0; j < 3; j++) {
    const b = await open("viewer-" + Math.random().toString(36).slice(2, 10));
    const got = new Promise(res => {
      const h = m => { const d = JSON.parse(m); if (d.type === "OFFER") { a.ws.off("message", h); res("OK"); } };
      a.ws.on("message", h);
      b.ws.on("message", m => { const d = JSON.parse(m); if (d.type === "EXPIRE") res("EXPIRE (servidor disse que a sala não existe)"); });
      setTimeout(() => res("nada chegou"), 8000);
    });
    b.ws.send(JSON.stringify({ type: "OFFER", dst: room, payload: { type: "data", connectionId: "dc_" + j, sdp: { type: "offer", sdp: "v=0" } } }));
    const r = await got; n++; if (r === "OK") ok++;
    console.log(`sala ${i+1} amigo ${j+1}: ${a.msgs.join(",")} / ${b.msgs.join(",")} -> ${r}`);
    b.ws.close();
  }
  a.ws.close();
}
console.log(`RESUMO: ${ok} de ${n} pedidos chegaram na sala`);
process.exit(0);
