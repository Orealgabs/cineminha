const { contextBridge, ipcRenderer } = require("electron");
contextBridge.exposeInMainWorld("picker", {
  onSources: fn => ipcRenderer.on("picker-sources", (e, data) => fn(data)),
  choose: id => ipcRenderer.send("picker-choice", id)
});
