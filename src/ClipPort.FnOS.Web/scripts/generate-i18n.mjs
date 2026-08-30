import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const languages = ['zh-CN', 'en-US', 'lzh'];
const decode = value => value
  .replaceAll('&lt;', '<')
  .replaceAll('&gt;', '>')
  .replaceAll('&quot;', '"')
  .replaceAll('&apos;', "'")
  .replaceAll('&amp;', '&')
  .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)))
  .replace(/&#x([0-9a-f]+);/gi, (_, code) => String.fromCodePoint(Number.parseInt(code, 16)));

const catalogs = {};
for (const language of languages) {
  const path = resolve(process.cwd(), '..', 'ClipPort', 'Strings', language, 'Resources.resw');
  const xml = await readFile(path, 'utf8');
  const values = {};
  for (const match of xml.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)) {
    values[decode(match[1])] = decode(match[2]);
  }
  catalogs[language] = values;
}

const output = `// Generated from the Windows .resw catalogs. Do not edit by hand.\n` +
  `export const windowsResources = ${JSON.stringify(catalogs, null, 2)} as const;\n`;
await writeFile(resolve(process.cwd(), 'src', 'generatedTranslations.ts'), output, 'utf8');
