import chalk from 'chalk';
import ora from 'ora';
import fs from 'fs-extra';
import path from 'path';
import { detectFramework } from '../utils/framework-detector';
import { getConfigValue } from './config';

export async function statusCommand() {
  console.log(chalk.cyan.bold('\n🔐 IAM System - Integration Status\n'));

  const cwd = process.cwd();
  const spinner = ora('Checking integration status...').start();

  const checks: Array<{ name: string; status: boolean; message: string }> = [];

  // Check 1: Detect framework
  const framework = await detectFramework(cwd);
  if (framework) {
    checks.push({
      name: 'Framework Detection',
      status: true,
      message: `Detected ${framework}`,
    });
  } else {
    checks.push({
      name: 'Framework Detection',
      status: false,
      message: 'No supported framework detected',
    });
  }

  // Check 2: SDK installed
  const packageJsonPath = path.join(cwd, 'package.json');
  if (await fs.pathExists(packageJsonPath)) {
    const packageJson = await fs.readJson(packageJsonPath);
    const hasSDK =
      packageJson.dependencies?.['@iam-system/sdk'] ||
      packageJson.devDependencies?.['@iam-system/sdk'];

    if (hasSDK) {
      const version =
        packageJson.dependencies?.['@iam-system/sdk'] ||
        packageJson.devDependencies?.['@iam-system/sdk'];
      checks.push({
        name: 'SDK Installation',
        status: true,
        message: `@iam-system/sdk ${version}`,
      });
    } else {
      checks.push({
        name: 'SDK Installation',
        status: false,
        message: '@iam-system/sdk not installed',
      });
    }
  } else {
    checks.push({
      name: 'SDK Installation',
      status: false,
      message: 'package.json not found',
    });
  }

  // Check 3: Authentication files exist
  const authFilesExist = await checkAuthFilesExist(cwd, framework);
  if (authFilesExist) {
    checks.push({
      name: 'Authentication Code',
      status: true,
      message: 'Authentication files found',
    });
  } else {
    checks.push({
      name: 'Authentication Code',
      status: false,
      message: 'Authentication files not found',
    });
  }

  // Check 4: API URL configuration
  const configApiUrl = await getConfigValue('apiUrl');
  if (configApiUrl) {
    checks.push({
      name: 'API Configuration',
      status: true,
      message: `API URL: ${configApiUrl}`,
    });
  } else {
    checks.push({
      name: 'API Configuration',
      status: false,
      message: 'API URL not configured (using defaults)',
    });
  }

  // Check 5: Try to connect to API
  const apiConnected = await checkApiConnection(configApiUrl || 'http://localhost:5161');
  if (apiConnected) {
    checks.push({
      name: 'API Connection',
      status: true,
      message: 'API is reachable',
    });
  } else {
    checks.push({
      name: 'API Connection',
      status: false,
      message: 'Cannot connect to API',
    });
  }

  spinner.stop();

  // Display results
  console.log(chalk.white('Integration Status:\n'));
  for (const check of checks) {
    const icon = check.status ? chalk.green('✓') : chalk.red('✗');
    const status = check.status ? chalk.green('PASS') : chalk.red('FAIL');
    console.log(`${icon} ${chalk.white(check.name.padEnd(25))} ${status}`);
    console.log(`  ${chalk.gray(check.message)}`);
  }
  console.log();

  // Overall status
  const allPassed = checks.every((c) => c.status);
  const passedCount = checks.filter((c) => c.status).length;

  if (allPassed) {
    console.log(chalk.green.bold('✅ All checks passed! IAM integration is complete.\n'));
  } else {
    console.log(
      chalk.yellow.bold(
        `⚠️  ${passedCount}/${checks.length} checks passed. Review failed checks above.\n`
      )
    );

    // Provide recommendations
    console.log(chalk.white('Recommendations:\n'));
    if (!framework) {
      console.log(chalk.gray('• Run'), chalk.cyan('iam init'), chalk.gray('to initialize a project'));
    }
    if (!checks.find((c) => c.name === 'SDK Installation')?.status) {
      console.log(
        chalk.gray('• Run'),
        chalk.cyan('iam add'),
        chalk.gray('to add IAM to existing project')
      );
    }
    if (!checks.find((c) => c.name === 'Authentication Code')?.status) {
      console.log(
        chalk.gray('• Run'),
        chalk.cyan('iam add'),
        chalk.gray('to generate authentication code')
      );
    }
    if (!checks.find((c) => c.name === 'API Connection')?.status) {
      console.log(chalk.gray('• Ensure your IAM API is running'));
      console.log(
        chalk.gray('• Check the API URL with'),
        chalk.cyan('iam config --key apiUrl')
      );
    }
    console.log();
  }
}

async function checkAuthFilesExist(cwd: string, framework: string | null): Promise<boolean> {
  if (!framework) return false;

  switch (framework) {
    case 'react':
      return await fs.pathExists(path.join(cwd, 'src', 'auth', 'AuthContext.tsx'));

    case 'vue':
      return await fs.pathExists(path.join(cwd, 'src', 'composables', 'useAuth.ts'));

    case 'nextjs':
      const hasAppDir = await fs.pathExists(
        path.join(cwd, 'app', 'api', 'auth', 'login', 'route.ts')
      );
      const hasPagesDir = await fs.pathExists(path.join(cwd, 'pages', 'api', 'auth', 'login.ts'));
      return hasAppDir || hasPagesDir;

    case 'angular':
      return await fs.pathExists(path.join(cwd, 'src', 'app', 'services', 'iam.service.ts'));

    default:
      return false;
  }
}

async function checkApiConnection(apiUrl: string): Promise<boolean> {
  try {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 5000); // 5 second timeout

    const response = await fetch(`${apiUrl}/health`, {
      signal: controller.signal,
    });

    clearTimeout(timeoutId);
    return response.ok;
  } catch {
    return false;
  }
}
