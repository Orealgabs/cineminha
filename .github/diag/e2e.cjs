// Duas pessoas de verdade (navegadores separados) entrando na sala pelos servidores públicos.
const { chromium } = require("playwright");
(async () => {
  const args = ["--use-fake-ui-for-media-stream","--use-fake-device-for-media-stream","--auto-select-desktop-capture-source=Entire screen","--autoplay-policy=no-user-gesture-required"];
  const b = await chromium.launch({ args });
  const errs = [];
  async function page(user){
    const ctx = await b.newContext(); const p = await ctx.newPage();
    p.on("pageerror", e => errs.push(user + ": " + e.message));
    await p.addInitScript(n => { localStorage.setItem("sessao.nome", n); localStorage.setItem("sessao.audio", "none"); }, user);
    await p.goto("http://localhost:8000/index.html"); await p.waitForTimeout(1500); return p;
  }
  for (let round = 1; round <= 3; round++) {
    const t0 = Date.now();
    const host = await page("Gabriel");
    await host.click("#goHost"); await host.click("#startBtn");
    await host.waitForFunction(() => /Transmitindo/.test(document.getElementById("hostStatusB").textContent), null, { timeout: 30000 });
    const code = (await host.textContent("#roomCode")).trim();
    const t1 = Date.now();
    const v = await page("Luuh");
    await v.fill("#joinCode", code); await v.click("#joinBtn");
    const ok = await v.waitForFunction(() => /ao vivo/.test(document.getElementById("viewStatusB").textContent), null, { timeout: 40000 }).then(() => true).catch(() => false);
    await v.waitForTimeout(4000);
    const vid = await v.$eval("video", x => x.videoWidth + "x" + x.videoHeight + " t=" + x.currentTime.toFixed(1));
    await v.fill("#chatInput", "oi"); await v.press("#chatInput", "Enter"); await host.waitForTimeout(1500);
    const chat = await host.$$eval("#chatList .cm", l => l.map(x => x.textContent.trim().slice(0, 30)));
    console.log(`RODADA ${round}: sala aberta em ${t1 - t0}ms | amigo ${ok ? "ASSISTINDO" : "FALHOU: " + await v.textContent("#viewStatusB")} em ${Date.now() - t1 - 5500}ms | vídeo ${vid} | conversa ${JSON.stringify(chat)}`);
    await host.context().close(); await v.context().close();
  }
  console.log("ERROS", JSON.stringify(errs)); await b.close(); process.exit(0);
})().catch(e => { console.error("FAIL", e.message); process.exit(1); });
