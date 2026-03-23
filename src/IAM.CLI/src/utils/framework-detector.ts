import fs from 'fs-extra';
import path from 'path';

export type Framework = 'react' | 'vue' | 'nextjs' | 'angular';

export async function detectFramework(cwd: string): Promise<Framework | null> {
  const packageJsonPath = path.join(cwd, 'package.json');

  if (!(await fs.pathExists(packageJsonPath))) {
    return null;
  }

  const packageJson = await fs.readJson(packageJsonPath);
  const deps = {
    ...packageJson.dependencies,
    ...packageJson.devDependencies,
  };

  // Next.js (check before React as Next includes React)
  if (deps.next) {
    return 'nextjs';
  }

  // React
  if (deps.react) {
    return 'react';
  }

  // Vue
  if (deps.vue) {
    return 'vue';
  }

  // Angular
  if (deps['@angular/core']) {
    return 'angular';
  }

  return null;
}
