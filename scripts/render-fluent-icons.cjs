// Optional asset authoring tool; shipping builds use the checked-in PNGs.
// Requires sharp (0.35.5). SVG originals and upstream attribution stay beside
// the renders. ImageIconSource cannot currently display SVG in IconSourceElement
// (microsoft/microsoft-ui-xaml#10081), so render at 256 px for high-DPI controls.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

(async () => {
  const directory = path.join(__dirname, '..', 'CloudInlet', 'Assets', 'Fluent');
  const names = (await fs.readdir(directory)).filter(name => name.endsWith('.svg')).sort();
  for (const name of names) {
    await sharp(path.join(directory, name), { density: 768 })
      .resize(256, 256).png().toFile(path.join(directory, name.replace(/\.svg$/, '.png')));
  }
  console.log(`Rendered ${names.length} Fluent icons at 256 px with sharp ${sharp.versions.sharp}.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
