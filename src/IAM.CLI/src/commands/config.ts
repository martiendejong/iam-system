import chalk from 'chalk';
import fs from 'fs-extra';
import path from 'path';
import os from 'os';

interface ConfigOptions {
  key?: string;
  value?: string;
  list?: boolean;
}

interface IamConfig {
  apiUrl?: string;
  defaultFramework?: string;
  [key: string]: any;
}

const CONFIG_DIR = path.join(os.homedir(), '.iam');
const CONFIG_FILE = path.join(CONFIG_DIR, 'config.json');

export async function configCommand(options: ConfigOptions) {
  console.log(chalk.cyan.bold('\n🔐 IAM System - Configuration\n'));

  // Ensure config directory exists
  await fs.ensureDir(CONFIG_DIR);

  // Initialize config file if it doesn't exist
  if (!(await fs.pathExists(CONFIG_FILE))) {
    await fs.writeJson(CONFIG_FILE, {}, { spaces: 2 });
  }

  if (options.list) {
    await listConfig();
  } else if (options.key && options.value) {
    await setConfig(options.key, options.value);
  } else if (options.key) {
    await getConfig(options.key);
  } else {
    await listConfig();
  }
}

async function listConfig(): Promise<void> {
  const config = await readConfig();
  const keys = Object.keys(config);

  if (keys.length === 0) {
    console.log(chalk.gray('No configuration set.\n'));
    console.log(chalk.white('Set configuration with:'));
    console.log(chalk.cyan('  iam config --key <key> --value <value>\n'));
    return;
  }

  console.log(chalk.white('Current configuration:\n'));
  for (const key of keys) {
    console.log(chalk.cyan(`  ${key}:`), chalk.white(config[key]));
  }
  console.log();
}

async function getConfig(key: string): Promise<void> {
  const config = await readConfig();

  if (config[key] !== undefined) {
    console.log(chalk.cyan(`${key}:`), chalk.white(config[key]));
    console.log();
  } else {
    console.log(chalk.yellow(`Configuration key "${key}" not found.\n`));
  }
}

async function setConfig(key: string, value: string): Promise<void> {
  const config = await readConfig();
  config[key] = value;

  await fs.writeJson(CONFIG_FILE, config, { spaces: 2 });

  console.log(chalk.green(`✓ Configuration updated:`));
  console.log(chalk.cyan(`  ${key}:`), chalk.white(value));
  console.log();
}

async function readConfig(): Promise<IamConfig> {
  if (await fs.pathExists(CONFIG_FILE)) {
    return await fs.readJson(CONFIG_FILE);
  }
  return {};
}

export async function getConfigValue(key: string): Promise<string | undefined> {
  const config = await readConfig();
  return config[key];
}
