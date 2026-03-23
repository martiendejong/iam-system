#!/usr/bin/env node

import { Command } from 'commander';
import chalk from 'chalk';
import { initCommand } from './commands/init';
import { addCommand } from './commands/add';
import { configCommand } from './commands/config';
import { statusCommand } from './commands/status';

const program = new Command();

program
  .name('iam')
  .description('IAM System CLI - Enterprise authentication in 60 seconds')
  .version('1.0.0');

program
  .command('init')
  .description('Initialize a new project with IAM authentication')
  .option('-f, --framework <framework>', 'Framework: react, vue, angular, nextjs')
  .option('-t, --typescript', 'Use TypeScript', true)
  .option('-d, --directory <directory>', 'Project directory', '.')
  .option('--api-url <url>', 'IAM API URL', 'http://localhost:5161')
  .action(initCommand);

program
  .command('add')
  .description('Add IAM authentication to an existing project')
  .option('-f, --framework <framework>', 'Auto-detect framework if not specified')
  .option('--api-url <url>', 'IAM API URL', 'http://localhost:5161')
  .action(addCommand);

program
  .command('config')
  .description('Configure IAM settings')
  .option('-k, --key <key>', 'Configuration key')
  .option('-v, --value <value>', 'Configuration value')
  .option('--list', 'List all configuration')
  .action(configCommand);

program
  .command('status')
  .description('Check IAM integration status')
  .action(statusCommand);

program.parse(process.argv);

// Show help if no command provided
if (!process.argv.slice(2).length) {
  console.log(chalk.cyan.bold('\n🔐 IAM System CLI\n'));
  console.log(chalk.white('Enterprise authentication in 60 seconds\n'));
  program.outputHelp();
  console.log(chalk.gray('\nExamples:'));
  console.log(chalk.gray('  $ iam init --framework react'));
  console.log(chalk.gray('  $ iam add'));
  console.log(chalk.gray('  $ iam status\n'));
}
