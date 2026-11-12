// Exposto à página do app: informa que ela está dentro do app e qual é o link opcional do site.
const { contextBridge, ipcRenderer } = require("electron");
let site = "";
try { site = ipcRenderer.sendSync("get-site") || ""; } catch (e) {}
contextBridge.exposeInMainWorld("sessaoApp", {
  isApp: true,
  version: "2.2.0",
  // tela/janela escolhida na última vez: { id: "window:<hwnd>:0" | "screen:<n>:0", name }
  lastSource: () => { try { return ipcRenderer.sendSync("last-source"); } catch (e) { return { id: "", name: "" }; } },
  siteUrl: site,
  changeSite: () => ipcRenderer.send("open-setup"),
  // atualização automática
  updateState: () => { try { return ipcRenderer.sendSync("update-state"); } catch (e) { return { state: "idle" }; } },
  onUpdate: fn => ipcRenderer.on("update", (e, st) => fn(st)),
  installUpdate: () => ipcRenderer.send("update-install")
});
// A tela de configuração (arquivo do próprio app) pode salvar o endereço do site.
if (location.protocol === "file:") {
  contextBridge.exposeInMainWorld("setup", {
    saveSite: url => ipcRenderer.send("setup-save", url),
    cancel: () => ipcRenderer.send("setup-cancel"),
    onError: fn => ipcRenderer.on("setup-error", (e, msg) => fn(msg))
  });
}
