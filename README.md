# Sessão de Série

App para transmitir a tela e o som para os amigos e assistirem juntos, cada um no seu sofá.

**Baixar:** [SessaoDeSerie-Instalador.exe](https://github.com/Orealgabs/cineminha/releases/latest/download/SessaoDeSerie-Instalador.exe)

Depois de instalado, o app se atualiza sozinho.

- `index.html`: versão para navegador.
- `app/`: o app para Windows (Electron). O instalador é montado e publicado pelo GitHub Actions quando a versão em `app/package.json` muda.
- `ajudante/`: código do Ajudante de Som, que captura só o som de um programa (ou tudo menos o Discord).
