// Sessão de Série — app para Windows.
// Tudo funciona dentro do app: transmitir e assistir. A captura da tela é feita pelo app,
// então o navegador onde a série está tocando não mostra a barra "Compartilhando esta guia".
const { app, BrowserWindow, session, desktopCapturer, ipcMain, shell, Menu } = require("electron");
const path = require("path");
const fs = require("fs");
const { spawn } = require("child_process");

// ---------- atualização automática (versões publicadas no GitHub) ----------
let updateState = { state: "idle" };
function sendUpdate(st){ updateState = st; if (win) win.webContents.send("update", st); }
function setupUpdates(){
  if (!app.isPackaged) return;
  let autoUpdater;
  try { ({ autoUpdater } = require("electron-updater")); } catch (e) { return; }
  autoUpdater.autoDownload = true;
  autoUpdater.autoInstallOnAppQuit = true;   // se a pessoa não reiniciar, instala ao fechar o app
  autoUpdater.on("update-available", info => sendUpdate({ state: "downloading", version: info.version, percent: 0 }));
  autoUpdater.on("download-progress", p => sendUpdate({ state: "downloading", version: updateState.version, percent: Math.round(p.percent || 0) }));
  autoUpdater.on("update-downloaded", info => sendUpdate({ state: "ready", version: info.version }));
  autoUpdater.on("error", () => { if (updateState.state === "downloading") sendUpdate({ state: "idle" }); });
  const check = () => autoUpdater.checkForUpdates().catch(() => {});
  setTimeout(check, 4000);
  setInterval(check, 2 * 60 * 60 * 1000);
  ipcMain.on("update-install", () => {
    stopHelper();
    setTimeout(() => autoUpdater.quitAndInstall(true, true), 400);
  });
}
ipcMain.on("update-state", e => { e.returnValue = updateState; });

app.setName("Sessao de Serie");
// A janela do app continua trabalhando normalmente mesmo coberta pela série em tela cheia.
app.commandLine.appendSwitch("disable-features", "LocalNetworkAccessChecks,CalculateNativeWinOcclusion");
app.commandLine.appendSwitch("disable-renderer-backgrounding");
app.commandLine.appendSwitch("disable-background-timer-throttling");
app.commandLine.appendSwitch("disable-backgrounding-occluded-windows");

if (!app.requestSingleInstanceLock()) { app.quit(); }

// ---------- configuração (endereço do site) ----------
const cfgFile = () => path.join(app.getPath("userData"), "config.json");
function readCfg() { try { return JSON.parse(fs.readFileSync(cfgFile(), "utf8")); } catch (e) { return {}; } }
function writeCfg(c) { try { fs.mkdirSync(path.dirname(cfgFile()), { recursive: true }); fs.writeFileSync(cfgFile(), JSON.stringify(c, null, 2)); } catch (e) {} }

function normalizeSite(raw) {
  let s = String(raw || "").trim();
  if (!s) return null;
  if (/^[a-z][a-z0-9+.-]*:/i.test(s) && !/^https:\/\//i.test(s)) return null; // só https
  if (!/^https:\/\//i.test(s)) s = "https://" + s;
  let u;
  try { u = new URL(s); } catch (e) { return null; }
  if (u.protocol !== "https:" || !u.hostname.includes(".")) return null;
  u.search = ""; u.hash = "";
  if (!u.pathname.endsWith("/") && !/\.html?$/i.test(u.pathname)) u.pathname += "/";
  return u.toString();
}

// ---------- ajudante de som embutido ----------
let helperProc = null;
function startHelper() {
  if (process.platform !== "win32") return;
  const exe = app.isPackaged ? path.join(process.resourcesPath, "AjudanteDeSom.exe") : path.join(__dirname, "AjudanteDeSom.exe");
  if (!fs.existsSync(exe)) return;
  try {
    helperProc = spawn(exe, [], { windowsHide: true, stdio: "ignore", detached: false });
    helperProc.on("error", () => { helperProc = null; });
    helperProc.on("exit", () => { helperProc = null; });
  } catch (e) { helperProc = null; }
}
function stopHelper() { try { if (helperProc) helperProc.kill(); } catch (e) {} helperProc = null; }

// ---------- janela principal ----------
let win = null;

const PAGE = () => path.join(__dirname, "page", "index.html");
function loadPage() { if (win) win.loadFile(PAGE()); }

function createWindow() {
  win = new BrowserWindow({
    width: 1320, height: 900, minWidth: 420, minHeight: 500,
    backgroundColor: "#121014",
    title: "Sessão de Série",
    icon: path.join(__dirname, "build", "icon.ico"),
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      sandbox: true,
      backgroundThrottling: false
    }
  });

  win.webContents.on("page-title-updated", e => e.preventDefault());

  // links para fora abrem no navegador normal
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: "deny" };
  });
  win.webContents.on("will-navigate", (e, url) => {
    if (url.startsWith("file:")) return;
    e.preventDefault();
    if (/^https?:/i.test(url)) shell.openExternal(url);
  });

  loadPage();
  win.on("closed", () => { win = null; });
}

function buildMenu() {
  const menu = Menu.buildFromTemplate([
    { label: "App", submenu: [
      { label: "Link para quem não tem o app (opcional)…", click: () => openSetup() },
      { label: "Recarregar", accelerator: "CmdOrCtrl+R", click: () => win && win.webContents.reload() },
      { label: "Tela cheia", accelerator: "F11", click: () => win && win.setFullScreen(!win.isFullScreen()) },
      { type: "separator" },
      { label: "Ferramentas de desenvolvedor", accelerator: "CmdOrCtrl+Shift+I", click: () => win && win.webContents.toggleDevTools() },
      { type: "separator" },
      { label: "Sair", role: "quit" }
    ]},
    { label: "Editar", submenu: [{ role: "cut", label: "Recortar" }, { role: "copy", label: "Copiar" }, { role: "paste", label: "Colar" }, { role: "selectAll", label: "Selecionar tudo" }] }
  ]);
  Menu.setApplicationMenu(menu);
}

// ---------- seletor de tela/janela ----------
let pickerOpen = null;
let lastSource = { id: "", name: "" }; // a última tela/janela escolhida, para o som vir dela
function showPicker(sources, withAudio) {
  if (pickerOpen) { try { pickerOpen.close(); } catch (e) {} }
  return new Promise(resolve => {
    const pw = new BrowserWindow({
      parent: win || undefined, modal: !!win, width: 900, height: 640, minWidth: 420, minHeight: 400,
      backgroundColor: "#121014", title: "Escolha o que transmitir", autoHideMenuBar: true, show: false, icon: path.join(__dirname, "build", "icon.ico"),
      webPreferences: { preload: path.join(__dirname, "picker-preload.js"), contextIsolation: true, sandbox: true }
    });
    pickerOpen = pw;
    let done = false;
    const finish = id => { if (done) return; done = true; pickerOpen = null; try { pw.close(); } catch (e) {} resolve(id); };
    const onChoice = (e, id) => { if (e.sender === pw.webContents) { ipcMain.removeListener("picker-choice", onChoice); finish(id || null); } };
    ipcMain.on("picker-choice", onChoice);
    pw.on("closed", () => { ipcMain.removeListener("picker-choice", onChoice); finish(null); });
    pw.loadFile(path.join(__dirname, "picker.html"));
    pw.webContents.once("did-finish-load", () => {
      pw.webContents.send("picker-sources", { sources, withAudio });
      pw.show();
    });
  });
}

function setupSession() {
  const ses = session.defaultSession;
  const allowed = new Set(["media", "display-capture", "fullscreen", "clipboard-sanitized-write", "clipboard-read",
    "local-network-access", "local-network", "loopback-network", "window-management"]);
  ses.setPermissionRequestHandler((wc, perm, cb) => cb(allowed.has(perm)));
  ses.setPermissionCheckHandler((wc, perm) => allowed.has(perm));

  ses.setDisplayMediaRequestHandler(async (request, callback) => {
    try {
      const raw = await desktopCapturer.getSources({ types: ["screen", "window"], thumbnailSize: { width: 400, height: 225 }, fetchWindowIcons: true });
      const ownId = win ? win.getMediaSourceId() : "";
      const list = raw.filter(s => s.id !== ownId && s.name !== "Escolha o que transmitir").map(s => ({
        id: s.id,
        name: s.name,
        screen: s.id.startsWith("screen:"),
        thumb: s.thumbnail && !s.thumbnail.isEmpty() ? s.thumbnail.toDataURL() : "",
        icon: s.appIcon && !s.appIcon.isEmpty() ? s.appIcon.toDataURL() : ""
      }));
      const id = await showPicker(list, !!request.audioRequested);
      const src = raw.find(s => s.id === id);
      if (!src) { callback({}); return; }
      lastSource = { id: src.id, name: src.name };
      const resp = { video: src };
      // som do PC inteiro (opção "Todo o som do PC"); a opção "Som de um programa" usa o ajudante
      if (request.audioRequested) resp.audio = "loopback";
      callback(resp);
    } catch (e) {
      callback({});
    }
  }, { useSystemPicker: false });
}

// ---------- mensagens das telas internas ----------
ipcMain.on("setup-save", (e, raw) => {
  const c = readCfg();
  if (String(raw || "").trim() === "") { delete c.site; writeCfg(c); loadPage(); return; }
  const site = normalizeSite(raw);
  if (!site) { e.sender.send("setup-error", "Endereço inválido. Ele deve começar com https://"); return; }
  c.site = site; writeCfg(c);
  loadPage();
});
ipcMain.on("setup-cancel", () => loadPage());
ipcMain.on("get-site", e => { e.returnValue = readCfg().site || ""; });
ipcMain.on("last-source", e => { e.returnValue = lastSource; });
function openSetup() {
  if (win) win.loadFile(path.join(__dirname, "setup.html"), { query: { site: readCfg().site || "", erro: "" } });
}
ipcMain.on("open-setup", openSetup);

app.on("second-instance", () => { if (win) { if (win.isMinimized()) win.restore(); win.focus(); } });
app.whenReady().then(() => {
  setupSession();
  buildMenu();
  startHelper();
  createWindow();
  setupUpdates();
});
app.on("window-all-closed", () => app.quit());
app.on("will-quit", stopHelper);
