// Optional asset authoring tool; shipping builds use the checked-in PNGs/ICO.
// Requires sharp, as does render-fluent-icons.cjs. Preserve the SVG artwork and
// render its tightly fitted viewBox at every native Windows icon resolution.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

(async () => {
  const directory = path.join(__dirname, '..', 'CloudInlet', 'Assets');
  const sizes = [16, 24, 32, 48, 64, 128, 256];
  const frames = [];
  for (const size of sizes) {
    const png = await sharp(path.join(directory, 'CloudInlet.svg'), { density: 768 })
      .resize(size, size).png().toBuffer();
    await fs.writeFile(path.join(directory, `CloudInlet-${size}.png`), png);
    frames.push(png);
  }

  // ICO entries contain their matching PNG frames, preserving 32-bit alpha.
  const header = Buffer.alloc(6 + sizes.length * 16);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  for (let index = 0; index < sizes.length; index++) {
    const entry = 6 + index * 16;
    header[entry] = header[entry + 1] = sizes[index] === 256 ? 0 : sizes[index];
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(frames[index].length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += frames[index].length;
  }
  await fs.writeFile(path.join(directory, 'CloudInlet.ico'), Buffer.concat([header, ...frames]));
  console.log(`Rendered ${sizes.length} app icon sizes and CloudInlet.ico.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
