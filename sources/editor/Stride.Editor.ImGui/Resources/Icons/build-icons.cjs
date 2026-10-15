// npm install sharp (or supply the absolute path of a bundled sharp module).
const fs = require('node:fs');
const path = require('node:path');
const sharp = require(process.argv[2] || 'sharp');
Promise.all(fs.readdirSync(__dirname).filter(name => name.endsWith('.svg')).map(name =>
  sharp(path.join(__dirname, name)).resize(48, 48).png().toFile(path.join(__dirname, name.replace(/\.svg$/, '.png')))
)).catch(error => { console.error(error); process.exitCode = 1; });
