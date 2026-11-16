// Testa servidores públicos de mensagens (MQTT por WebSocket) como ponto de encontro.
import mqtt from "mqtt";
const brokers = [
  "wss://broker.emqx.io:8084/mqtt",
  "wss://broker.hivemq.com:8884/mqtt",
  "wss://test.mosquitto.org:8081/mqtt",
  "wss://mqtt.eclipseprojects.io:443/mqtt",
  "wss://public.mqtthq.com:8084/mqtt",
];
async function test(url){
  const t0 = Date.now(); const topic = "sessaodeserie/diag/" + Math.random().toString(36).slice(2);
  const opts = { connectTimeout: 8000, reconnectPeriod: 0, clean: true };
  const a = mqtt.connect(url, opts), b = mqtt.connect(url, opts);
  const up = c => new Promise(r => { c.once("connect", () => r(true)); c.once("error", () => r(false)); setTimeout(() => r(false), 9000); });
  const [ua, ub] = await Promise.all([up(a), up(b)]);
  if (!ua || !ub) { a.end(true); b.end(true); return `${url}: NÃO conectou`; }
  await new Promise(r => a.subscribe(topic, r));
  const big = "x".repeat(12000);
  const got = new Promise(r => { a.on("message", (t, m) => r(m.length)); setTimeout(() => r(0), 6000); });
  const t1 = Date.now(); b.publish(topic, big);
  const len = await got; const ms = Date.now() - t1;
  a.end(true); b.end(true);
  return `${url}: conectou em ${t1 - t0}ms, ${len ? "repassou 12KB em " + ms + "ms" : "NÃO repassou"}`;
}
for (let k = 0; k < 2; k++) for (const u of brokers) console.log("RES", await test(u));
process.exit(0);
