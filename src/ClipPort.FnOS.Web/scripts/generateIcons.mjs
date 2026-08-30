import { mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import sharp from 'sharp';

const [source, packageRoot] = process.argv.slice(2);
if (!source || !packageRoot) {
  throw new Error('Usage: node generateIcons.mjs <source.png> <package-root>');
}

const outputs = [
  ['ICON.PNG', 120],
  ['ICON_256.PNG', 256],
  [join('app', 'ui', 'images', 'icon_64.png'), 64],
  [join('app', 'ui', 'images', 'icon_256.png'), 256],
  [join('app', 'server', 'wwwroot', 'icon.png'), 256],
];

for (const [relativePath, size] of outputs) {
  const output = join(packageRoot, relativePath);
  await mkdir(dirname(output), { recursive: true });
  await sharp(source).resize(size, size, { fit: 'contain' }).png().toFile(output);
}
