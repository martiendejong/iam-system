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

interface InitOptions {
  framework?: string;
  typescript?: boolean;
  directory?: string;
  apiUrl?: string;
}

export async function initCommand(options: InitOptions) {
  console.log(chalk.cyan.bold('\n🔐 IAM System - Initialize Authentication\n'));

  const cwd = path.resolve(options.directory || '.');

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

  // Step 3: Install SDK
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

  // Step 4: Generate authentication code
  spinner.start('Generating authentication code...');
  try {
    await generateAuthCode(framework, cwd, apiUrl, options.typescript ?? true);
    spinner.succeed('Authentication code generated');
  } catch (error) {
    spinner.fail('Failed to generate code');
    console.error(chalk.red(error));
    process.exit(1);
  }

  // Step 5: Success message
  console.log();
  console.log(chalk.green.bold('✅ IAM authentication initialized successfully!\n'));
  console.log(chalk.white('Next steps:\n'));
  console.log(chalk.gray(`1. Start your IAM API at ${apiUrl}`));
  console.log(chalk.gray(`2. Import and use the authentication components`));
  console.log(chalk.gray(`3. Run ${chalk.cyan('iam status')} to verify integration\n`));

  printUsageExample(framework);
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

function printUsageExample(framework: Framework) {
  console.log(chalk.white('Usage example:\n'));

  switch (framework) {
    case 'react':
      console.log(chalk.gray('// src/App.tsx'));
      console.log(chalk.cyan(`import { AuthProvider, useAuth } from './auth';

function App() {
  return (
    <AuthProvider>
      <LoginPage />
    </AuthProvider>
  );
}

function LoginPage() {
  const { login, isAuthenticated, user } = useAuth();

  if (isAuthenticated) {
    return <div>Welcome, {user?.email}!</div>;
  }

  return <button onClick={() => login('user@example.com', 'password')}>Login</button>;
}`));
      break;

    case 'vue':
      console.log(chalk.gray('// src/main.ts'));
      console.log(chalk.cyan(`import { useAuth } from './composables/useAuth';

const { login, isAuthenticated, user } = useAuth();

await login('user@example.com', 'password');
console.log('Logged in:', user.value?.email);`));
      break;

    case 'nextjs':
      console.log(chalk.gray('// app/api/auth/login/route.ts'));
      console.log(chalk.cyan(`import { iamClient } from '@/lib/iam';

export async function POST(request: Request) {
  const { email, password } = await request.json();
  const response = await iamClient.login(email, password);
  return Response.json({ user: response.user });
}`));
      break;

    case 'angular':
      console.log(chalk.gray('// src/app/login/login.component.ts'));
      console.log(chalk.cyan(`import { IamService } from './services/iam.service';

constructor(private iam: IamService) {}

async login() {
  const response = await this.iam.login('user@example.com', 'password');
  console.log('Logged in:', response.user.email);
}`));
      break;
  }

  console.log();
}
