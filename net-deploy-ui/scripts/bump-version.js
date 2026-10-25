const fs = require('fs');
const path = require('path');

const versionFile = path.join(__dirname, '..', 'src', 'environments', 'version.ts');
let buildNumber = 1;
const now = new Date();
const dateStr = now.toISOString().replace('T', ' ').substring(0, 19);

if (fs.existsSync(versionFile)) {
  const content = fs.readFileSync(versionFile, 'utf8');
  const match = content.match(/BUILD_NUMBER\s*=\s*(\d+)/);
  if (match) {
    buildNumber = parseInt(match[1], 10) + 1;
  }
}

const fileContent = `// Automatically generated on each build - do not edit manually
export const APP_VERSION = '1.0.${buildNumber}';
export const BUILD_NUMBER = ${buildNumber};
export const BUILD_TIMESTAMP = '${dateStr}';
`;

fs.writeFileSync(versionFile, fileContent, 'utf8');
console.log(`[Version Bump] New Version: v1.0.${buildNumber} (${dateStr})`);
