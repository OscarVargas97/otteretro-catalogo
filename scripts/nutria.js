// Genera los iconos de la nutria (pixel art 16×16 del sistema de diseño) como PNG e ICO.
// Uso: node scripts/nutria.js   → src/Otteretro/nutria.ico y web/public/nutria.png
const fs = require("fs"), path = require("path"), zlib = require("zlib");

const filas = [
  "................",
  "..OO.OOOOOO.OO..",
  ".OFFOFFFFFFOFFO.",
  "..OFFFFFFFFFFO..",
  ".OFFFFFFFFFFFFO.",
  "OFFFDDFFFFDDFFFO",
  "OFFFDWFFFFDWFFFO",
  "OFFLLLLLLLLLLFFO",
  "OFLLLLLDDLLLLLFO",
  "OFLLLLLLLLLLLLFO",
  ".OLLLDLLLLDLLLO.",
  ".OFLLLDDDDLLLFO.",
  "..OFLLLLLLLLFO..",
  "...OOFFFFFFOO...",
  ".....OOOOOO.....",
  "................",
];
const color = { O: [0x2a, 0x1a, 0x10], F: [0x9a, 0x64, 0x40], L: [0xe2, 0xbf, 0x96], D: [0x2a, 0x1a, 0x10], W: [0xff, 0xff, 0xff] };

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function bloque(tipo, datos) {
  const len = Buffer.alloc(4); len.writeUInt32BE(datos.length);
  const td = Buffer.concat([Buffer.from(tipo), datos]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
function png(tam) {
  const escala = tam / 16, crudo = Buffer.alloc((tam * 4 + 1) * tam);
  for (let y = 0; y < tam; y++) {
    crudo[y * (tam * 4 + 1)] = 0;
    for (let x = 0; x < tam; x++) {
      const c = color[filas[Math.floor(y / escala)][Math.floor(x / escala)]];
      const i = y * (tam * 4 + 1) + 1 + x * 4;
      if (c) { crudo[i] = c[0]; crudo[i + 1] = c[1]; crudo[i + 2] = c[2]; crudo[i + 3] = 255; }
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(tam, 0); ihdr.writeUInt32BE(tam, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    bloque("IHDR", ihdr), bloque("IDAT", zlib.deflateSync(crudo)), bloque("IEND", Buffer.alloc(0))]);
}
function ico(tamanos) {
  const imgs = tamanos.map(png), cab = Buffer.alloc(6 + 16 * imgs.length);
  cab.writeUInt16LE(1, 2); cab.writeUInt16LE(imgs.length, 4);
  let desp = cab.length;
  imgs.forEach((img, i) => {
    const e = 6 + 16 * i, t = tamanos[i];
    cab[e] = t === 256 ? 0 : t; cab[e + 1] = t === 256 ? 0 : t;
    cab.writeUInt16LE(1, e + 4); cab.writeUInt16LE(32, e + 6);
    cab.writeUInt32LE(img.length, e + 8); cab.writeUInt32LE(desp, e + 12);
    desp += img.length;
  });
  return Buffer.concat([cab, ...imgs]);
}
const raiz = path.join(__dirname, "..");
fs.writeFileSync(path.join(raiz, "src/Otteretro/nutria.ico"), ico([16, 32, 48, 256]));
fs.mkdirSync(path.join(raiz, "web/public"), { recursive: true });
fs.writeFileSync(path.join(raiz, "web/public/nutria.png"), png(128));
console.log("nutria.ico y nutria.png generados");
