import { prompt } from 'enquirer';
import chalk from 'chalk';
import ora from 'ora';
import { execa } from 'execa';
import fs from 'fs-extra';
import path from 'path';
import { detectFramework, Framework } from '../utils/framework-detector';
import { generateReactAuth } from '../generators/react';
import { generateVueAuth } from '../generators/vue';
import { generateNextjsAuth } from '../generators/nextjs';
import { generateAngularAuth } from '../generators/angular';

interface AddOptions {
  framework?: string;
  apiUrl?: string;
}

export async function addCommand(options: AddOptions) {
  console.log(chalk.cyan.bold('\n🔐 IAM System - Add Authentication to Existing Project\n'));

  const cwd = process.cwd();

  // Step 1: Detect or ask for framework
  let framework: Framework;
  if (options.framework) {
    framework = options.framework as Framework;
  } else {
    const detected = await detectFramework(cwd);
    if (detected) {
      const { confirm } = await prompt<{ confirm: boolean }>({
        type: 'confirm',
        name: 'confirm',
        message: `Detected ${chalk.cyan(detected)}. Is this correct?`,
        initial: true,
      });

      framework = confirm ? detected : await askFramework();
    } else {
      framework = await askFramework();
    }
  }

  // Step 2: Confirm API URL
  const apiUrl = await confirmApiUrl(options.apiUrl || 'http://localhost:5161');

  // Step 3: Check if SDK is already installed
  const packageJsonPath = path.join(cwd, 'package.json');
  const packageJson = await fs.readJson(packageJsonPath);
  const hasSDK =
    packageJson.dependencies?.['@iam-system/sdk'] ||
    packageJson.devDependencies?.['@iam-system/sdk'];

  if (!hasSDK) {
    console.log();
    const spinner = ora('Installing @iam-system/sdk...').start();
    try {
      await installSdk(cwd);
      spinner.succeed('SDK installed successfully');
    } catch (error) {
      spinner.fail('Failed to install SDK');
      console.error(chalk.red(error));
      process.exit(1);
    }
  } else {
    console.log(chalk.green('\n✓ @iam-system/sdk already installed\n'));
  }

  // Step 4: Generate authentication code
  const spinner = ora('Adding authentication code...').start();
  try {
    await generateAuthCode(framework, cwd, apiUrl, true);
    spinner.succeed('Authentication code added');
  } catch (error) {
    spinner.fail('Failed to generate code');
    console.error(chalk.red(error));
    process.exit(1);
  }

  // Step 5: Success message
  console.log();
  console.log(chalk.green.bold('✅ IAM authentication added successfully!\n'));
  console.log(chalk.white('Next steps:\n'));
  console.log(chalk.gray(`1. Start your IAM API at ${apiUrl}`));
  console.log(chalk.gray(`2. Import and use the authentication components`));
  console.log(chalk.gray(`3. Run ${chalk.cyan('iam status')} to verify integration\n`));

  printIntegrationInstructions(framework);
}

async function askFramework(): Promise<Framework> {
  const { framework } = await prompt<{ framework: Framework }>({
    type: 'select',
    name: 'framework',
    message: 'Select your framework:',
    choices: [
      { name: 'react', message: 'React' },
      { name: 'vue', message: 'Vue.js' },
      { name: 'nextjs', message: 'Next.js' },
      { name: 'angular', message: 'Angular' },
    ],
  });
  return framework;
}

async function confirmApiUrl(defaultUrl: string): Promise<string> {
  const { apiUrl } = await prompt<{ apiUrl: string }>({
    type: 'input',
    name: 'apiUrl',
    message: 'IAM API URL:',
    initial: defaultUrl,
  });
  return apiUrl;
}

async function installSdk(cwd: string): Promise<void> {
  // Detect package manager
  const hasYarn = await fs.pathExists(path.join(cwd, 'yarn.lock'));
  const hasPnpm = await fs.pathExists(path.join(cwd, 'pnpm-lock.yaml'));

  const pkgManager = hasPnpm ? 'pnpm' : hasYarn ? 'yarn' : 'npm';
  const installCmd = pkgManager === 'npm' ? 'install' : 'add';

  await execa(pkgManager, [installCmd, '@iam-system/sdk'], { cwd });
}

async function generateAuthCode(
  framework: Framework,
  cwd: string,
  apiUrl: string,
  typescript: boolean
): Promise<void> {
  const config = { apiUrl, typescript };

  switch (framework) {
    case 'react':
      await generateReactAuth(cwd, config);
      break;
    case 'vue':
      await generateVueAuth(cwd, config);
      break;
    case 'nextjs':
      await generateNextjsAuth(cwd, config);
      break;
    case 'angular':
      await generateAngularAuth(cwd, config);
      break;
  }
}

function printIntegrationInstructions(framework: Framework) {
  console.log(chalk.white('Integration instructions:\n'));

  switch (framework) {
    case 'react':
      console.log(chalk.gray('// src/App.tsx'));
      console.log(chalk.cyan(`import { AuthProvider } from './auth';

function App() {
  return (
    <AuthProvider>
      {/* Your app components */}
    </AuthProvider>
  );
}`));
      break;

    case 'vue':
      console.log(chalk.gray('// src/components/YourComponent.vue'));
      console.log(chalk.cyan(`<script setup>
import { useAuth } from '@/composables/useAuth';

const { user, isAuthenticated, login, logout } = useAuth();
</script>`));
      break;

    case 'nextjs':
      console.log(chalk.gray('// Your Next.js components can now use:'));
      console.log(chalk.cyan(`// API routes in app/api/auth/
// - POST /api/auth/login
// - POST /api/auth/register
// - POST /api/auth/refresh
// - POST /api/auth/logout`));
      break;

    case 'angular':
      console.log(chalk.gray('// app.module.ts - Add to providers:'));
      console.log(chalk.cyan(`import { HTTP_INTERCEPTORS } from '@angular/common/http';
import { AuthInterceptor } from './interceptors/auth.interceptor';

providers: [
  { provide: HTTP_INTERCEPTORS, useClass: AuthInterceptor, multi: true }
]`));
      break;
  }

  console.log();
}
