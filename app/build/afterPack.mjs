// Depois de empacotar: coloca o ícone da Sessão de Série no executável de dentro do app
// (é o ícone que aparece na barra de tarefas e no Gerenciador de Tarefas).
import fs from "node:fs";
import path from "node:path";
import * as ResEdit from "resedit";
import * as PELibrary from "pe-library";

export default async function afterPack(context) {
  if (context.electronPlatformName !== "win32") return;
  const exe = path.join(context.appOutDir, `${context.packager.appInfo.productFilename}.exe`);
  const data = fs.readFileSync(exe);
  const pe = PELibrary.NtExecutable.from(data, { ignoreCert: true });
  const res = PELibrary.NtExecutableResource.from(pe);
  const ico = ResEdit.Data.IconFile.from(fs.readFileSync(path.join(context.packager.projectDir, "build", "icon.ico")));
  const groups = ResEdit.Resource.IconGroupEntry.fromEntries(res.entries);
  const ids = groups.length ? groups.map(g => ({ id: g.id, lang: g.lang })) : [{ id: 1, lang: 1033 }];
  for (const { id, lang } of ids) {
    ResEdit.Resource.IconGroupEntry.replaceIconsForResource(res.entries, id, lang, ico.icons.map(i => i.data));
  }
  // nome do programa nas propriedades do arquivo
  const vi = ResEdit.Resource.VersionInfo.fromEntries(res.entries)[0];
  if (vi) {
    const lang = vi.getAllLanguagesForStringValues()[0] || { lang: 1033, codepage: 1200 };
    vi.setStringValues(lang, { FileDescription: "Sessão de Série", ProductName: "Sessão de Série", CompanyName: "Sessão de Série" });
    vi.outputToResourceEntries(res.entries);
  }
  res.outputResource(pe);
  fs.writeFileSync(exe, Buffer.from(pe.generate()));
  console.log("  • ícone aplicado em", path.basename(exe));
}
